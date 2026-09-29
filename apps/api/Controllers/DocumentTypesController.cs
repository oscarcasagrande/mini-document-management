using DocReader.Api.Contracts.V1;
using DocReader.Api.Errors;
using DocReader.Api.Mapping;
using DocReader.Application.Catalog;
using DocReader.Application.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DocReader.Api.Controllers;

/// <summary>
/// Registry of the document types classification and extraction consult at runtime (Configuração Dinâmica).
/// Editing a type's rules here applies to the next document processed, without a deploy.
/// </summary>
[ApiController]
[Route("api/v1/document-types")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError, ProblemTypes.ContentType)]
public sealed class DocumentTypesController(
    DocumentTypeService service,
    IOptions<PagingOptions> pagingOptions) : ControllerBase
{
    /// <summary>Creates a document type.</summary>
    /// <remarks>The code is unique without regard to case, is stored in upper case and cannot be changed later.</remarks>
    /// <param name="request">Code, name, schema, classification and extraction rules, and the active flag.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="201">Created. The <c>Location</c> header points at it.</response>
    /// <response code="400">The code, the name or one of the JSON fields is invalid.</response>
    /// <response code="409">The code is already taken.</response>
    [HttpPost]
    [ProducesResponseType(typeof(DocumentTypeResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    public async Task<ActionResult<DocumentTypeResponse>> CreateAsync(
        [FromBody] CreateDocumentTypeRequest request,
        CancellationToken ct)
    {
        var created = await service.CreateAsync(
            request.Code,
            request.Name,
            request.Schema.GetRawText(),
            request.ClassificationRules.GetRawText(),
            request.ExtractionRules.GetRawText(),
            request.Active,
            ct);

        return Created($"/api/v1/document-types/{created.Id}", ConfigurationResponseMapper.ToResponse(created));
    }

    /// <summary>Lists the document types, by code.</summary>
    /// <param name="request">Filters and paging.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">One page of document types.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<DocumentTypeResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<DocumentTypeResponse>>> ListAsync(
        [FromQuery] DocumentTypeListRequest request,
        CancellationToken ct)
    {
        var paging = pagingOptions.Value;
        var pageSize = Math.Clamp(request.PageSize ?? paging.DefaultPageSize, 1, paging.MaxPageSize);

        var page = await service.ListAsync(
            new DocumentTypeFilter(request.Code, request.Name, request.Active, Math.Max(1, request.Page), pageSize),
            ct);

        return Ok(ConfigurationResponseMapper.ToPage(page, ConfigurationResponseMapper.ToResponse));
    }

    /// <summary>Returns one document type.</summary>
    /// <param name="id">Identity.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">The document type.</response>
    /// <response code="404">There is none with this id.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DocumentTypeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<ActionResult<DocumentTypeResponse>> GetAsync(Guid id, CancellationToken ct) =>
        Ok(ConfigurationResponseMapper.ToResponse(await service.GetAsync(id, ct)));

    /// <summary>Changes the name, the schema and the classification and extraction rules of a document type.</summary>
    /// <remarks>
    /// The code is immutable. Classification consults these rules on the next document; a document already
    /// processed keeps its recorded type until it is reprocessed or reclassified.
    /// </remarks>
    /// <param name="id">Identity.</param>
    /// <param name="request">New name, schema, rules and active flag.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">Updated.</response>
    /// <response code="400">The name or one of the JSON fields is invalid.</response>
    /// <response code="404">There is none with this id.</response>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(DocumentTypeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<ActionResult<DocumentTypeResponse>> UpdateAsync(
        Guid id,
        [FromBody] UpdateDocumentTypeRequest request,
        CancellationToken ct)
    {
        var updated = await service.UpdateAsync(
            id,
            request.Name,
            request.Schema.GetRawText(),
            request.ClassificationRules.GetRawText(),
            request.ExtractionRules.GetRawText(),
            request.Active,
            ct);

        return Ok(ConfigurationResponseMapper.ToResponse(updated));
    }

    /// <summary>Deletes a document type that nothing refers to.</summary>
    /// <remarks>
    /// One of the seven built-in types cannot be deleted, only deactivated. A custom type with documents or
    /// retention policies pointing at its code cannot be deleted either.
    /// </remarks>
    /// <param name="id">Identity.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="404">There is none with this id.</response>
    /// <response code="409">It is a built-in type, or something still refers to it.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);

        return NoContent();
    }
}
