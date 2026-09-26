using System.Text.Json.Nodes;
using DocReader.Api.Contracts.V1;
using DocReader.Api.Errors;
using DocReader.Api.Mapping;
using DocReader.Application.Options;
using DocReader.Application.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DocReader.Api.Controllers;

/// <summary>
/// Where documents are stored. A document is stored in the repository of its product or service, or in the default one,
/// and stays there.
/// </summary>
[ApiController]
[Route("api/v1/storage-repositories")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError, ProblemTypes.ContentType)]
public sealed class StorageRepositoriesController(
    StorageRepositoryService service,
    IOptions<PagingOptions> pagingOptions) : ControllerBase
{
    /// <summary>Creates a storage repository.</summary>
    /// <remarks>
    /// FILE_SYSTEM and DATABASE are implemented. AZURE_BLOB_STORAGE and AWS_S3 are registered so a repository can be
    /// configured for them, but their adapters are not implemented yet: uploading to one answers 501, and they cannot be
    /// the default. The connection settings are validated per provider, encrypted before they are stored, and never
    /// returned by any endpoint.
    /// </remarks>
    /// <param name="request">Code, name, provider, settings and flags.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="201">Created. The <c>Location</c> header points at it.</response>
    /// <response code="400">The code, the name or the settings are invalid for the provider.</response>
    /// <response code="409">The code is already taken.</response>
    /// <response code="422">The repository cannot be the default (provider not implemented, or inactive).</response>
    [HttpPost]
    [ProducesResponseType(typeof(StorageRepositoryResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity, ProblemTypes.ContentType)]
    public async Task<ActionResult<StorageRepositoryResponse>> CreateAsync(
        [FromBody] CreateStorageRepositoryRequest request,
        CancellationToken ct)
    {
        var created = await service.CreateAsync(
            request.Code,
            request.Name,
            request.Provider!.Value,
            ToJsonObject(request.ConnectionConfig),
            request.IsDefault,
            request.Active,
            ct);

        return Created($"/api/v1/storage-repositories/{created.Id}", ConfigurationResponseMapper.ToResponse(created));
    }

    /// <summary>Lists the storage repositories, the default first. The settings are never included.</summary>
    /// <param name="request">Filters and paging.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">One page of repositories.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<StorageRepositoryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<StorageRepositoryResponse>>> ListAsync(
        [FromQuery] StorageRepositoryListRequest request,
        CancellationToken ct)
    {
        var paging = pagingOptions.Value;
        var pageSize = Math.Clamp(request.PageSize ?? paging.DefaultPageSize, 1, paging.MaxPageSize);

        var page = await service.ListAsync(
            new StorageRepositoryFilter(request.Code, request.Provider, request.Active, Math.Max(1, request.Page), pageSize),
            ct);

        return Ok(ConfigurationResponseMapper.ToPage(page, ConfigurationResponseMapper.ToResponse));
    }

    /// <summary>Returns one storage repository. The settings are never included.</summary>
    /// <param name="id">Identity.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">The repository.</response>
    /// <response code="404">There is none with this id.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(StorageRepositoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<ActionResult<StorageRepositoryResponse>> GetAsync(Guid id, CancellationToken ct) =>
        Ok(ConfigurationResponseMapper.ToResponse(await service.GetAsync(id, ct)));

    /// <summary>Changes a storage repository.</summary>
    /// <remarks>
    /// The code and the provider are fixed. <c>connectionConfig</c> is a partial update: a key with a string sets it, a key
    /// with <c>null</c> removes it, the others stay. The <c>directory</c> of a FILE_SYSTEM repository cannot change once
    /// documents are stored in it. Exactly one repository is the default: to change it, make another one the default;
    /// asking the current default to stop being one, or deactivating it, answers 409.
    /// </remarks>
    /// <param name="id">Identity.</param>
    /// <param name="request">Name, flags and the settings to change.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">Updated.</response>
    /// <response code="400">The name or the settings are invalid for the provider.</response>
    /// <response code="404">There is none with this id.</response>
    /// <response code="409">It is the default, or documents are stored in it and the change would strand them.</response>
    /// <response code="422">The repository cannot be the default (provider not implemented, or inactive).</response>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(StorageRepositoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity, ProblemTypes.ContentType)]
    public async Task<ActionResult<StorageRepositoryResponse>> UpdateAsync(
        Guid id,
        [FromBody] UpdateStorageRepositoryRequest request,
        CancellationToken ct)
    {
        var updated = await service.UpdateAsync(
            id,
            request.Name,
            request.Active,
            request.IsDefault,
            ToJsonObject(request.ConnectionConfig),
            ct);

        return Ok(ConfigurationResponseMapper.ToResponse(updated));
    }

    /// <summary>Deletes a storage repository that nothing uses.</summary>
    /// <remarks>The default repository cannot be deleted. A repository with documents, or that a product or service names, cannot either: deactivate it instead.</remarks>
    /// <param name="id">Identity.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="404">There is none with this id.</response>
    /// <response code="409">It is the default, or something still uses it.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        await service.DeleteAsync(id, ct);

        return NoContent();
    }

    /// <summary>A JSON object of strings, with the nulls kept: they are how a partial update removes a key.</summary>
    private static JsonObject? ToJsonObject(Dictionary<string, string?>? config)
    {
        if (config is null)
        {
            return null;
        }

        var result = new JsonObject();
        foreach (var (key, value) in config)
        {
            result[key] = value is null ? null : JsonValue.Create(value);
        }

        return result;
    }
}
