using DocReader.Api.Audit;
using DocReader.Api.Contracts.V1;
using DocReader.Api.Errors;
using DocReader.Api.Mapping;
using DocReader.Api.Security;
using DocReader.Application.Backup;
using DocReader.Application.Errors;
using DocReader.Domain.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocReader.Api.Controllers;

/// <summary>Restores a backup archive over the current database and document storage.</summary>
/// <remarks>
/// Requires the <c>docreader-admin</c> role (<c>OIDC_ADMIN_ROLE</c>) once OIDC is configured; anonymous when it is not
/// (ADR 0003).
/// </remarks>
[ApiController]
[Route("api/v1/admin/restore")]
[Produces("application/json")]
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized, ProblemTypes.ContentType)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden, ProblemTypes.ContentType)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError, ProblemTypes.ContentType)]
public sealed class AdminRestoreController(RestoreService service) : ControllerBase
{
    /// <summary>Largest archive the endpoint reads: 2 GiB.</summary>
    public const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Uploads a backup archive and starts restoring it. Replaces the current data.</summary>
    /// <remarks>
    /// The archive is verified before anything is queued: the signature of its checksum list (it must come from an
    /// installation with the same <c>STORAGE_CONFIG_ENCRYPTION_KEY</c>) and the SHA-256 of every file in it; any
    /// mismatch answers 400. Then the worker, with the API in read-only mode (writes answer 503, reads keep working),
    /// replays the SQL dump with <c>psql --single-transaction</c> (the first error rolls the whole restore back and the
    /// database stays as it was), checks that every document of the manifest exists, and writes the archived files back.
    /// Everything the backup carried replaces what is there now; documents created since are gone. Backup and restore
    /// jobs are not part of a backup and are kept.
    /// </remarks>
    /// <param name="request">The <c>.tar.gz</c>, in the multipart field <c>file</c>.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="202">Verified and accepted. The <c>Location</c> header points at the job.</response>
    /// <response code="400">No file, or the archive is unreadable, altered, incomplete or signed with another key.</response>
    /// <response code="409">Another restore is pending or running.</response>
    /// <response code="413">The archive is larger than 2 GiB.</response>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxArchiveBytes + (1024 * 1024))]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxArchiveBytes + (1024 * 1024))]
    [Audited(AuditActionTypes.RestoreStarted, "RestoreJob")]
    [ProducesResponseType(typeof(RestoreJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge, ProblemTypes.ContentType)]
    public async Task<ActionResult<RestoreJobResponse>> CreateAsync([FromForm] RestoreBackupRequest request, CancellationToken ct)
    {
        var file = request.File;
        if (file is null || file.Length == 0)
        {
            throw new RequestValidationException("MISSING_FILE", "Send the backup archive in the multipart field named file.");
        }

        await using var archive = file.OpenReadStream();
        var job = await service.EnqueueAsync(archive, ct);

        return Accepted(BackupResponseMapper.RestoreJobUrl(job.Id), BackupResponseMapper.ToResponse(job));
    }

    /// <summary>Returns the status of a restore.</summary>
    /// <param name="id">Identity of the job.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">The job.</response>
    /// <response code="404">There is no restore with this id.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RestoreJobResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<ActionResult<RestoreJobResponse>> GetAsync(Guid id, CancellationToken ct) =>
        Ok(BackupResponseMapper.ToResponse(await service.GetAsync(id, ct)));
}
