using DocReader.Api.Contracts.V1;
using DocReader.Api.Errors;
using DocReader.Api.Mapping;
using DocReader.Api.Security;
using DocReader.Application.Backup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DocReader.Api.Controllers;

/// <summary>
/// Backups of the database and of the locally stored document files. A backup is a <c>.tar.gz</c> with a plain SQL
/// dump, a storage manifest, the document files of FILE_SYSTEM and DATABASE repositories, and a signed SHA-256 list of
/// everything in it; restore it with <c>POST /api/v1/admin/restore</c>.
/// </summary>
/// <remarks>
/// Requires the <c>docreader-admin</c> role (<c>OIDC_ADMIN_ROLE</c>) once OIDC is configured; anonymous when it is not
/// (ADR 0003).
/// </remarks>
[ApiController]
[Route("api/v1/admin/backup")]
[Produces("application/json")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized, ProblemTypes.ContentType)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden, ProblemTypes.ContentType)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError, ProblemTypes.ContentType)]
public sealed class AdminBackupController(BackupService service) : ControllerBase
{
    /// <summary>Starts a backup.</summary>
    /// <remarks>
    /// Asynchronous: this records the job and returns; the worker runs <c>pg_dump</c>, builds the archive and saves it in
    /// the default storage repository or in the one named in <c>storageRepositoryId</c> (an AZURE_BLOB_STORAGE or AWS_S3
    /// repository makes an off-site copy). Poll the <c>Location</c> until the status is COMPLETED or FAILED. Documents
    /// stored in cloud repositories are listed in the manifest but their files are not downloaded into the archive.
    /// Refused with 503 while a restore is running.
    /// </remarks>
    /// <param name="request">Optional target repository; the body may be omitted.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="202">Accepted. The <c>Location</c> header points at the job.</response>
    /// <response code="422">The named repository does not exist, is inactive, or is a DATABASE repository.</response>
    /// <response code="503">A restore is running and the system is read-only.</response>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(BackupJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable, ProblemTypes.ContentType)]
    public async Task<ActionResult<BackupJobResponse>> CreateAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateBackupRequest? request,
        CancellationToken ct)
    {
        var job = await service.EnqueueAsync(request?.StorageRepositoryId, ct);

        return Accepted(BackupResponseMapper.JobUrl(job.Id), BackupResponseMapper.ToResponse(job));
    }

    /// <summary>Returns the status of a backup.</summary>
    /// <param name="id">Identity of the job.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">The job. Once COMPLETED it carries the size, the SHA-256 and the download URL of the archive.</response>
    /// <response code="404">There is no backup with this id.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(BackupJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<ActionResult<BackupJobResponse>> GetAsync(Guid id, CancellationToken ct) =>
        Ok(BackupResponseMapper.ToResponse(await service.GetAsync(id, ct)));

    /// <summary>Downloads the archive of a completed backup.</summary>
    /// <remarks>The archive holds personal data from the documents: keep it as protected as the database itself.</remarks>
    /// <param name="id">Identity of the job.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">The <c>.tar.gz</c>.</response>
    /// <response code="404">There is no backup with this id, or its archive is gone from the repository.</response>
    /// <response code="409">The backup is not completed.</response>
    [HttpGet("{id:guid}/content")]
    [Produces(BackupService.ArchiveMimeType, "application/problem+json")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK, BackupService.ArchiveMimeType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    public async Task<IActionResult> DownloadAsync(Guid id, CancellationToken ct)
    {
        var (job, content) = await service.OpenArchiveAsync(id, ct);

        return File(content, BackupService.ArchiveMimeType, job.FileName);
    }
}
