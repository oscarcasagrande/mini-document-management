using DocReader.Api.Contracts.V1;
using DocReader.Api.Errors;
using DocReader.Api.Mapping;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Application.Options;
using DocReader.Domain;
using DocReader.Domain.Documents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DocReader.Api.Controllers;

/// <summary>
/// Ingestion and consultation of documents. Every endpoint is anonymous in this proof of concept:
/// there is no login, no bearer token and no tenant.
/// </summary>
[ApiController]
[Route("api/v1/documents")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError, ProblemTypes.ContentType)]
public sealed class DocumentsController(
    DocumentUploadService uploadService,
    DocumentQueryService queryService,
    DocumentDeletionService deletionService,
    DocumentReprocessingService reprocessingService,
    IOptions<PagingOptions> pagingOptions,
    IOptions<UploadOptions> uploadOptions,
    IOptions<ProcessingQueueOptions> queueOptions) : ControllerBase
{
    /// <summary>
    /// Receives a document and queues it for processing.
    /// </summary>
    /// <remarks>
    /// The response is asynchronous on purpose: the API stores the original, issues the protocol and
    /// answers <c>202 Accepted</c> with a <c>Location</c> header, while OCR, classification and
    /// extraction run in the worker. Poll <c>statusUrl</c> to follow the document.
    ///
    /// Accepted formats are PDF, PNG, JPEG and TIFF, checked by the real file signature rather than
    /// by the announced content type. Size and page limits are configurable and answer 413 and 422.
    ///
    /// Send the optional <c>Idempotency-Key</c> header to make a retry safe: the same key with the
    /// same file replays the original response, and the same key with a different file answers 409.
    /// </remarks>
    /// <param name="request">Multipart body carrying the file and its optional metadata.</param>
    /// <param name="idempotencyKey">Optional replay protection key.</param>
    /// <param name="channel">
    /// Origin of the upload, <c>WEB</c> or <c>API</c>. The interface sends WEB; anything else
    /// defaults to API.
    /// </param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="202">Document accepted, stored and queued.</response>
    /// <response code="400">The request is malformed or a field is invalid.</response>
    /// <response code="409">The Idempotency-Key was already used with a different file.</response>
    /// <response code="413">The file is above the configured size limit.</response>
    /// <response code="415">The real signature is not an accepted format, or contradicts the declared type.</response>
    /// <response code="422">The file is an accepted format but unreadable or above the page limit.</response>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(UploadAcceptedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status415UnsupportedMediaType, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity, ProblemTypes.ContentType)]
    public async Task<IActionResult> UploadAsync(
        [FromForm] UploadDocumentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromHeader(Name = "X-Upload-Channel")] string? channel,
        CancellationToken ct)
    {
        var file = request.File;
        if (file is null || file.Length == 0)
        {
            throw new UploadRejectedException(
                UploadRejectionReason.MissingFile,
                "MISSING_FILE",
                "Send the document in the multipart field named file.");
        }

        // Rejected here as well as in the service, so a body already buffered by Kestrel is refused
        // before it is read, hashed and paged.
        if (file.Length > uploadOptions.Value.MaxSizeBytes)
        {
            throw new UploadRejectedException(
                UploadRejectionReason.TooLarge,
                "FILE_TOO_LARGE",
                $"The uploaded file has {file.Length} bytes and the limit is {uploadOptions.Value.MaxSizeBytes} bytes.");
        }

        await using var content = file.OpenReadStream();

        var command = new UploadDocumentCommand(
            content,
            file.FileName,
            file.ContentType,
            request.ExpectedDocumentType,
            request.ExternalReference,
            ParseChannel(channel),
            idempotencyKey);

        var result = await uploadService.UploadAsync(command, ct);
        var response = DocumentResponseMapper.ToAcceptedResponse(result);

        if (result.Replayed)
        {
            Response.Headers["Idempotency-Replayed"] = "true";
        }

        return Accepted(response.DocumentUrl, response);
    }

    /// <summary>
    /// Lists uploaded documents, newest first.
    /// </summary>
    /// <remarks>
    /// Every filter is optional and they combine with AND. <c>page</c> starts at 1 and
    /// <c>pageSize</c> is capped by the configured maximum. The envelope carries
    /// <c>totalCount</c> and <c>totalPages</c> so a client can paginate without guessing.
    /// </remarks>
    /// <param name="request">Filters and paging.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">Page of documents, possibly empty.</response>
    /// <response code="400">A filter value is invalid.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<DocumentSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest, ProblemTypes.ContentType)]
    public async Task<ActionResult<PagedResponse<DocumentSummaryResponse>>> ListAsync(
        [FromQuery] DocumentListRequest request,
        CancellationToken ct)
    {
        var paging = pagingOptions.Value;
        var pageSize = Math.Clamp(request.PageSize ?? paging.DefaultPageSize, 1, paging.MaxPageSize);

        var filter = new DocumentListFilter(
            request.Protocol,
            request.FileName,
            request.DocumentType,
            ParseOptionalEnum<UploadChannel>(request.Channel, nameof(request.Channel)),
            ParseOptionalEnum<DocumentStatus>(request.Status, nameof(request.Status)),
            request.UploadedFrom,
            request.UploadedTo,
            Math.Max(1, request.Page),
            pageSize);

        var page = await queryService.ListAsync(filter, ct);

        return Ok(DocumentResponseMapper.ToPagedResponse(page));
    }

    /// <summary>
    /// Returns the consolidated view of one document, including its processing timeline.
    /// </summary>
    /// <param name="id">Identity of the document.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">Document found.</response>
    /// <response code="404">No document with this id.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DocumentDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<ActionResult<DocumentDetailResponse>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var snapshot = await queryService.GetSnapshotAsync(id, includeEvents: true, ct);

        return Ok(DocumentResponseMapper.ToDetail(snapshot, queueOptions.Value.MaxAttempts));
    }

    /// <summary>
    /// Returns the consolidated view of one document addressed by its human readable protocol.
    /// </summary>
    /// <param name="protocol">Protocol in the form <c>DOC-yyyyMMdd-NNNNNN</c>.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">Document found.</response>
    /// <response code="404">No document with this protocol, or the protocol is malformed.</response>
    [HttpGet("by-protocol/{protocol}")]
    [ProducesResponseType(typeof(DocumentDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<ActionResult<DocumentDetailResponse>> GetByProtocolAsync(
        string protocol,
        CancellationToken ct)
    {
        var document = await queryService.GetByProtocolAsync(protocol, includeEvents: false, ct);
        var snapshot = await queryService.GetSnapshotAsync(document.Id, includeEvents: true, ct);

        return Ok(DocumentResponseMapper.ToDetail(snapshot, queueOptions.Value.MaxAttempts));
    }

    /// <summary>
    /// Returns the light status payload, meant for polling.
    /// </summary>
    /// <param name="id">Identity of the document.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">Current status.</response>
    /// <response code="404">No document with this id.</response>
    [HttpGet("{id:guid}/status")]
    [ProducesResponseType(typeof(DocumentStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<ActionResult<DocumentStatusResponse>> GetStatusAsync(Guid id, CancellationToken ct)
    {
        var snapshot = await queryService.GetSnapshotAsync(id, includeEvents: false, ct);

        return Ok(DocumentResponseMapper.ToStatus(snapshot, queueOptions.Value.MaxAttempts));
    }

    /// <summary>
    /// Serves the original file exactly as it was received.
    /// </summary>
    /// <remarks>
    /// The default is <c>Content-Disposition: inline</c>, so a browser renders the PDF or the image
    /// in place. Add <c>?download=true</c> to force a download. The original stays available even
    /// when processing failed, which is a hard requirement of the PoC.
    /// </remarks>
    /// <param name="id">Identity of the document.</param>
    /// <param name="download">True to answer with an attachment instead of inline content.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">The original bytes.</response>
    /// <response code="404">No document with this id.</response>
    /// <response code="410">Metadata exists but the stored file is gone.</response>
    [HttpGet("{id:guid}/content")]
    [Produces("application/pdf", "image/png", "image/jpeg", "image/tiff", "application/problem+json")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone, ProblemTypes.ContentType)]
    public async Task<IActionResult> GetContentAsync(
        Guid id,
        [FromQuery] bool download,
        CancellationToken ct)
    {
        var content = await queryService.OpenContentAsync(id, ct);

        var disposition = new ContentDispositionHeaderValue(download ? "attachment" : "inline");
        disposition.SetHttpFileName(content.FileName);
        Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();

        var entityTag = new EntityTagHeaderValue($"\"{content.Sha256}\"", isWeak: false);

        return File(
            content.Content,
            content.MimeType,
            lastModified: null,
            entityTag,
            enableRangeProcessing: true);
    }

    /// <summary>
    /// Returns the raw text the OCR read, page by page.
    /// </summary>
    /// <remarks>
    /// The text is exactly what the OCR produced, in reading order, with nothing corrected or
    /// interpreted. It belongs to the latest extraction: while a reprocessing runs, the previous text
    /// stays available and <c>status</c> tells what is happening now. Answers 409 while there is
    /// nothing to return yet, or when every attempt failed.
    /// </remarks>
    /// <param name="id">Identity of the document.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">Text of the latest extraction.</response>
    /// <response code="404">No document with this id.</response>
    /// <response code="409">The document has no result yet, or its processing failed.</response>
    [HttpGet("{id:guid}/text")]
    [ProducesResponseType(typeof(DocumentTextResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    public async Task<ActionResult<DocumentTextResponse>> GetTextAsync(Guid id, CancellationToken ct)
    {
        var text = await queryService.GetTextAsync(id, ct);

        return Ok(DocumentResponseMapper.ToText(text));
    }

    /// <summary>
    /// Explains how the document was classified, for debugging a document that came out UNKNOWN.
    /// </summary>
    /// <remarks>
    /// Runs the classifier again, with the rules in force now, over the text of the latest extraction. For
    /// every document type it tried, it reports the score, the threshold, each evidence found or missing
    /// (with the rule pattern that matched and whether OCR errors had to be tolerated) and a sentence
    /// saying why the type was or was not accepted. <c>recorded</c> is what was stored when the document
    /// was processed; it differs from <c>current</c> after the rules changed, until the document is
    /// reprocessed. Nothing is written. The raw text is included by default because it is the first thing
    /// to look at; it is the same content as <c>/text</c>, and <c>includeText=false</c> leaves it out.
    /// Answers 409 while there is nothing to return yet, or when every attempt failed.
    /// </remarks>
    /// <param name="id">Identity of the document.</param>
    /// <param name="includeText">Whether to include the raw OCR text. Defaults to true.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">The classification, explained.</response>
    /// <response code="404">No document with this id.</response>
    /// <response code="409">The document has no result yet, or its processing failed.</response>
    [HttpGet("{id:guid}/classification-diagnostics")]
    [ProducesResponseType(typeof(ClassificationDiagnosticsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    public async Task<ActionResult<ClassificationDiagnosticsResponse>> GetClassificationDiagnosticsAsync(
        Guid id,
        [FromQuery] bool includeText = true,
        CancellationToken ct = default)
    {
        var diagnostics = await queryService.GetClassificationDiagnosticsAsync(id, ct);

        return Ok(DocumentResponseMapper.ToClassificationDiagnostics(diagnostics, includeText));
    }

    /// <summary>
    /// Returns the canonical structured result.
    /// </summary>
    /// <remarks>
    /// Each field carries the value as read (<c>raw</c>), the normalized value, a confidence between
    /// 0 and 1, a validation status (<c>VALID</c>, <c>INVALID</c>, <c>NOT_FOUND</c> or
    /// <c>UNCERTAIN</c>) with machine readable messages such as <c>CHECK_DIGIT_VALID</c>, and the
    /// evidence (page and bounding box in pixels of the analysed page image). A field that was not
    /// found is null, never invented. Answers 409 while there is nothing to return yet, or when
    /// every attempt failed.
    /// </remarks>
    /// <param name="id">Identity of the document.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">Result of the latest extraction.</response>
    /// <response code="404">No document with this id.</response>
    /// <response code="409">The document has no result yet, or its processing failed.</response>
    [HttpGet("{id:guid}/result")]
    [ProducesResponseType(typeof(DocumentResultResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    public async Task<ActionResult<DocumentResultResponse>> GetResultAsync(Guid id, CancellationToken ct)
    {
        var result = await queryService.GetResultAsync(id, ct);

        return Ok(DocumentResponseMapper.ToResult(result));
    }

    /// <summary>
    /// Runs the pipeline again on a document that is already stored.
    /// </summary>
    /// <remarks>
    /// Creates a new processing attempt and a new extraction; previous results are kept. The original
    /// file is never touched. A document that is already queued or being processed answers 409, so
    /// the same document is never processed twice at once.
    /// </remarks>
    /// <param name="id">Identity of the document.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="202">Queued again. Poll <c>statusUrl</c> to follow it.</response>
    /// <response code="404">No document with this id.</response>
    /// <response code="409">The document is already queued or being processed.</response>
    [HttpPost("{id:guid}/reprocess")]
    [ProducesResponseType(typeof(UploadAcceptedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    public async Task<IActionResult> ReprocessAsync(Guid id, CancellationToken ct)
    {
        var document = await reprocessingService.ReprocessAsync(id, ct);
        var links = DocumentResponseMapper.LinksFor(document.Id);

        var response = new UploadAcceptedResponse(
            document.Id,
            document.Protocol,
            document.Status,
            links.Status,
            links.Self,
            links.Content);

        return Accepted(links.Self, response);
    }

    /// <summary>
    /// Deletes a document permanently.
    /// </summary>
    /// <remarks>
    /// Removes the row, the timeline, the processing jobs, the idempotency keys bound to it and the
    /// stored original. There is no soft delete and no recovery; the interface asks for confirmation
    /// before calling this endpoint.
    /// </remarks>
    /// <param name="id">Identity of the document.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="204">Document and file removed.</response>
    /// <response code="404">No document with this id.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        await deletionService.DeleteAsync(id, ct);

        return NoContent();
    }

    private static UploadChannel ParseChannel(string? value) =>
        EnumNaming.TryParse<UploadChannel>(value, out var parsed) ? parsed : UploadChannel.Api;

    private static TEnum? ParseOptionalEnum<TEnum>(string? value, string fieldName)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (EnumNaming.TryParse<TEnum>(value, out var parsed))
        {
            return parsed;
        }

        throw new InvalidFilterException(
            fieldName,
            $"Filter {fieldName} accepts only: {string.Join(", ", EnumNaming.NamesOf<TEnum>())}.");
    }
}
