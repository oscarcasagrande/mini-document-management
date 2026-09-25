using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using DocReader.Domain.Documents;
using Microsoft.Extensions.Logging;

namespace DocReader.Application.Documents;

/// <summary>
/// Read side of the API: listing, detail, status and original content. All of it works without
/// login (RF-005, RF-006) and stays available even when processing failed.
/// </summary>
public sealed class DocumentQueryService(
    IDocumentRepository repository,
    IFileStorage storage,
    ILogger<DocumentQueryService> logger)
{
    public Task<PagedResult<Document>> ListAsync(DocumentListFilter filter, CancellationToken ct) =>
        repository.ListAsync(filter, ct);

    public async Task<Document> GetByIdAsync(Guid id, bool includeEvents, CancellationToken ct)
    {
        var document = await repository.FindByIdAsync(id, includeEvents, ct).ConfigureAwait(false);
        return document ?? throw new DocumentNotFoundException(id.ToString());
    }

    public async Task<Document> GetByProtocolAsync(string protocol, bool includeEvents, CancellationToken ct)
    {
        if (!DocumentProtocol.IsWellFormed(protocol))
        {
            throw new DocumentNotFoundException(protocol);
        }

        var document = await repository.FindByProtocolAsync(protocol, includeEvents, ct).ConfigureAwait(false);
        return document ?? throw new DocumentNotFoundException(protocol);
    }

    /// <summary>
    /// The document with its latest job and extraction summary: what the status and detail views need
    /// to show progress, the last error and the identity of the result.
    /// </summary>
    public async Task<DocumentSnapshot> GetSnapshotAsync(Guid id, bool includeEvents, CancellationToken ct)
    {
        var document = await GetByIdAsync(id, includeEvents, ct).ConfigureAwait(false);
        var job = await repository.FindLatestJobAsync(id, ct).ConfigureAwait(false);
        var extraction = await repository.FindLatestExtractionSummaryAsync(id, ct).ConfigureAwait(false);

        return new DocumentSnapshot(document, job, extraction);
    }

    /// <summary>
    /// The structured result of the latest extraction. While a reprocessing runs, the previous result
    /// stays available (RF-013); the document status in the answer says what is happening now.
    /// </summary>
    /// <exception cref="ResultNotReadyException">There is no extraction yet, or every attempt failed.</exception>
    public async Task<DocumentResult> GetResultAsync(Guid id, CancellationToken ct)
    {
        var document = await GetByIdAsync(id, includeEvents: false, ct).ConfigureAwait(false);
        var result = await repository.FindLatestExtractionResultAsync(id, ct).ConfigureAwait(false);

        return result is null
            ? throw new ResultNotReadyException(id, document.Status)
            : new DocumentResult(document, result);
    }

    /// <summary>The raw text, page by page, of the latest extraction.</summary>
    /// <exception cref="ResultNotReadyException">There is no extraction yet, or every attempt failed.</exception>
    public async Task<DocumentText> GetTextAsync(Guid id, CancellationToken ct)
    {
        var document = await GetByIdAsync(id, includeEvents: false, ct).ConfigureAwait(false);
        var text = await repository.FindLatestExtractionTextAsync(id, ct).ConfigureAwait(false);

        return text is null
            ? throw new ResultNotReadyException(id, document.Status)
            : new DocumentText(document, text);
    }

    /// <summary>
    /// Opens the original bytes. The metadata is authoritative for the content type, so a renamed
    /// or mislabeled upload is still served as what it really is.
    /// </summary>
    public async Task<DocumentContentResult> OpenContentAsync(Guid id, CancellationToken ct)
    {
        var document = await GetByIdAsync(id, includeEvents: false, ct).ConfigureAwait(false);

        Stream content;
        try
        {
            content = await storage.OpenReadAsync(document.StorageKey, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            logger.LogError(
                "Stored blob is missing for an existing document. documentId={DocumentId} storageKey={StorageKey}",
                document.Id,
                document.StorageKey);
            throw new DocumentContentMissingException(document.Id);
        }

        return new DocumentContentResult(
            content,
            document.MimeType,
            document.OriginalFileName,
            document.SizeBytes,
            document.Sha256);
    }
}
