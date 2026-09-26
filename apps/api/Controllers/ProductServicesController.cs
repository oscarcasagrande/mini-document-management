using DocReader.Api.Contracts.V1;
using DocReader.Api.Errors;
using DocReader.Api.Mapping;
using DocReader.Application.Catalog;
using DocReader.Application.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DocReader.Api.Controllers;

/// <summary>
/// Registry of the products and services a document can be uploaded for.
/// </summary>
[ApiController]
[Route("api/v1/product-services")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError, ProblemTypes.ContentType)]
public sealed class ProductServicesController(
    ProductServiceService service,
    IOptions<PagingOptions> pagingOptions) : ControllerBase
{
    /// <summary>Creates a product or service.</summary>
    /// <remarks>The code is unique without regard to case, is stored in upper case and cannot be changed later.</remarks>
    /// <param name="request">Code, name and active flag.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="201">Created. The <c>Location</c> header points at it.</response>
    /// <response code="400">The code or the name is invalid.</response>
    /// <response code="409">The code is already taken.</response>
    /// <response code="422">The storage repository does not exist or is inactive.</response>
    [HttpPost]
    [ProducesResponseType(typeof(ProductServiceResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity, ProblemTypes.ContentType)]
    public async Task<ActionResult<ProductServiceResponse>> CreateAsync(
        [FromBody] CreateProductServiceRequest request,
        CancellationToken ct)
    {
        var created = await service.CreateAsync(request.Code, request.Name, request.Active, request.StorageRepositoryId, ct);

        return Created($"/api/v1/product-services/{created.Id}", ConfigurationResponseMapper.ToResponse(created));
    }

    /// <summary>Lists the products and services, by code.</summary>
    /// <param name="request">Filters and paging.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">One page of products and services.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<ProductServiceResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<ProductServiceResponse>>> ListAsync(
        [FromQuery] ProductServiceListRequest request,
        CancellationToken ct)
    {
        var paging = pagingOptions.Value;
        var pageSize = Math.Clamp(request.PageSize ?? paging.DefaultPageSize, 1, paging.MaxPageSize);

        var page = await service.ListAsync(
            new ProductServiceFilter(request.Code, request.Name, request.Active, Math.Max(1, request.Page), pageSize),
            ct);

        return Ok(ConfigurationResponseMapper.ToPage(page, ConfigurationResponseMapper.ToResponse));
    }

    /// <summary>Returns one product or service.</summary>
    /// <param name="id">Identity.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">The product or service.</response>
    /// <response code="404">There is none with this id.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ProductServiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<ActionResult<ProductServiceResponse>> GetAsync(Guid id, CancellationToken ct) =>
        Ok(ConfigurationResponseMapper.ToResponse(await service.GetAsync(id, ct)));

    /// <summary>Changes the name, the active flag and the storage repository of a product or service.</summary>
    /// <remarks>The code is immutable. <c>storageRepositoryId</c> replaces the current one (omit it to go back to the default repository); documents already stored stay where they are. Deactivating a product does not touch its documents; it refuses new uploads.</remarks>
    /// <param name="id">Identity.</param>
    /// <param name="request">New name and active flag.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">Updated.</response>
    /// <response code="400">The name is invalid.</response>
    /// <response code="404">There is none with this id.</response>
    /// <response code="422">The storage repository does not exist or is inactive.</response>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ProductServiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity, ProblemTypes.ContentType)]
    public async Task<ActionResult<ProductServiceResponse>> UpdateAsync(
        Guid id,
        [FromBody] UpdateProductServiceRequest request,
        CancellationToken ct) =>
        Ok(ConfigurationResponseMapper.ToResponse(await service.UpdateAsync(id, request.Name, request.Active, request.StorageRepositoryId, ct)));

    /// <summary>Deletes a product or service that nothing refers to.</summary>
    /// <remarks>A product with documents, retention policies or webhook subscriptions cannot be deleted: deactivate it instead.</remarks>
    /// <param name="id">Identity.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="404">There is none with this id.</response>
    /// <response code="409">Something still refers to it.</response>
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
