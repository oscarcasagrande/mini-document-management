using DocReader.Api.Contracts.V1;
using DocReader.Api.Errors;
using DocReader.Api.Mapping;
using DocReader.Application.Options;
using DocReader.Application.Retention;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DocReader.Api.Controllers;

/// <summary>
/// How long documents are kept. The most specific policy that applies to a document decides its purge date.
/// </summary>
[ApiController]
[Route("api/v1/retention-policies")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError, ProblemTypes.ContentType)]
public sealed class RetentionPoliciesController(
    RetentionPolicyService service,
    IOptions<PagingOptions> pagingOptions) : ControllerBase
{
    /// <summary>Creates a retention policy for a document type, a product or service, or both.</summary>
    /// <remarks>
    /// Precedence, most specific first: type and product together, then the product alone, then the type alone,
    /// then the global policy (both omitted), which always exists. There is one policy per scope.
    /// Creating or changing a policy does not recalculate the purge date of documents that already have one:
    /// they pick it up when reprocessed.
    /// </remarks>
    /// <param name="request">The scope and the retention in days.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="201">Created. The <c>Location</c> header points at it.</response>
    /// <response code="400">The document type is unknown or the days are out of range.</response>
    /// <response code="409">A policy for this scope already exists (there is exactly one global policy).</response>
    /// <response code="422">The product or service does not exist.</response>
    [HttpPost]
    [ProducesResponseType(typeof(RetentionPolicyResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity, ProblemTypes.ContentType)]
    public async Task<ActionResult<RetentionPolicyResponse>> CreateAsync(
        [FromBody] CreateRetentionPolicyRequest request,
        CancellationToken ct)
    {
        var created = await service.CreateAsync(request.DocumentType, request.ProductServiceId, request.RetentionDays ?? 0, ct);

        return Created($"/api/v1/retention-policies/{created.Id}", ConfigurationResponseMapper.ToResponse(created));
    }

    /// <summary>Lists the retention policies, the global one first.</summary>
    /// <param name="request">Filters and paging.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">One page of policies.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<RetentionPolicyResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<RetentionPolicyResponse>>> ListAsync(
        [FromQuery] RetentionPolicyListRequest request,
        CancellationToken ct)
    {
        var paging = pagingOptions.Value;
        var pageSize = Math.Clamp(request.PageSize ?? paging.DefaultPageSize, 1, paging.MaxPageSize);

        var page = await service.ListAsync(
            new RetentionPolicyFilter(request.DocumentType, request.ProductServiceId, Math.Max(1, request.Page), pageSize),
            ct);

        return Ok(ConfigurationResponseMapper.ToPage(page, ConfigurationResponseMapper.ToResponse));
    }

    /// <summary>Returns one retention policy.</summary>
    /// <param name="id">Identity.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">The policy.</response>
    /// <response code="404">There is none with this id.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RetentionPolicyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<ActionResult<RetentionPolicyResponse>> GetAsync(Guid id, CancellationToken ct) =>
        Ok(ConfigurationResponseMapper.ToResponse(await service.GetAsync(id, ct)));

    /// <summary>Changes the number of days of a policy.</summary>
    /// <remarks>The scope is the identity of a policy and cannot change. Documents that already have a purge date keep it until they are reprocessed.</remarks>
    /// <param name="id">Identity.</param>
    /// <param name="request">The new retention in days.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">Updated.</response>
    /// <response code="400">The days are out of range.</response>
    /// <response code="404">There is none with this id.</response>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(RetentionPolicyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<ActionResult<RetentionPolicyResponse>> UpdateAsync(
        Guid id,
        [FromBody] UpdateRetentionPolicyRequest request,
        CancellationToken ct) =>
        Ok(ConfigurationResponseMapper.ToResponse(await service.UpdateAsync(id, request.RetentionDays ?? 0, ct)));

    /// <summary>Deletes a retention policy.</summary>
    /// <remarks>The global policy cannot be deleted. Documents that carried the deleted policy keep their purge date.</remarks>
    /// <param name="id">Identity.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="404">There is none with this id.</response>
    /// <response code="409">It is the global policy.</response>
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
