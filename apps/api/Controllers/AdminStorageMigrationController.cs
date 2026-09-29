using DocReader.Api.Contracts.V1;
using DocReader.Api.Errors;
using DocReader.Api.Mapping;
using DocReader.Application.StorageMigrations;
using DocReader.Domain.StorageMigrations;
using Microsoft.AspNetCore.Mvc;

namespace DocReader.Api.Controllers;

/// <summary>
/// Moves documents from one storage repository to another, in the background. The originals are copied, never
/// deleted from the source.
/// </summary>
[ApiController]
[Route("api/v1/admin/storage-migration")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError, ProblemTypes.ContentType)]
public sealed class AdminStorageMigrationController(StorageMigrationService service) : ControllerBase
{
    /// <summary>Starts moving documents from one storage repository to another.</summary>
    /// <remarks>
    /// Asynchronous: this only records the job and returns it as PENDING; a worker copies the matching documents in
    /// batches of 10, repoints each one at its copy and records a STORAGE_MIGRATED event on its timeline (which the
    /// document's <c>migrationHistory</c> shows). The original stays in the source repository: nothing is deleted.
    /// A document that fails to copy (an unreachable or misconfigured target, a missing original) stays where it was
    /// and is counted in <c>documentsFailed</c>; the others go on. Without <c>documentFilter</c>, every document
    /// currently on the source is moved; purged documents never are.
    /// </remarks>
    /// <param name="request">Source, target and the optional filter.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="202">Accepted. The <c>Location</c> header points at the job; poll it for progress.</response>
    /// <response code="400">The body is invalid, the source and the target are the same, or the date range is inverted.</response>
    /// <response code="422">The source or the target does not exist, or the target is inactive.</response>
    [HttpPost]
    [ProducesResponseType(typeof(StorageMigrationJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity, ProblemTypes.ContentType)]
    public async Task<ActionResult<StorageMigrationJobResponse>> StartAsync(
        [FromBody] CreateStorageMigrationRequest request,
        CancellationToken ct)
    {
        var filter = request.DocumentFilter is { } documentFilter
            ? new StorageMigrationFilter(
                documentFilter.DocumentType,
                documentFilter.ProductServiceId,
                documentFilter.UploadedFrom,
                documentFilter.UploadedTo)
            : null;

        var job = await service.EnqueueAsync(request.SourceRepositoryId!.Value, request.TargetRepositoryId!.Value, filter, ct);

        return Accepted($"/api/v1/admin/storage-migration/{job.Id}", ConfigurationResponseMapper.ToResponse(job));
    }

    /// <summary>Returns one storage migration job, with its progress.</summary>
    /// <param name="jobId">Identity of the job.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">The job.</response>
    /// <response code="404">There is none with this id.</response>
    [HttpGet("{jobId:guid}")]
    [ProducesResponseType(typeof(StorageMigrationJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<ActionResult<StorageMigrationJobResponse>> GetAsync(Guid jobId, CancellationToken ct) =>
        Ok(ConfigurationResponseMapper.ToResponse(await service.GetAsync(jobId, ct)));

    /// <summary>Cancels a pending or running storage migration job.</summary>
    /// <remarks>
    /// Stops the job before its next batch and marks it CANCELLED. Documents already moved by earlier batches are not
    /// moved back: they keep pointing at the target (whose copy they now use) and the source still has their original.
    /// A job that already ended (COMPLETED, FAILED or CANCELLED) answers 409.
    /// </remarks>
    /// <param name="jobId">Identity of the job.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">Cancelled; the job as it is now.</response>
    /// <response code="404">There is none with this id.</response>
    /// <response code="409">The job already ended.</response>
    [HttpDelete("{jobId:guid}/rollback")]
    [ProducesResponseType(typeof(StorageMigrationJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    public async Task<ActionResult<StorageMigrationJobResponse>> RollbackAsync(Guid jobId, CancellationToken ct) =>
        Ok(ConfigurationResponseMapper.ToResponse(await service.CancelAsync(jobId, ct)));
}
