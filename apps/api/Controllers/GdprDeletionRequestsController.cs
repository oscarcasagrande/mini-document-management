using DocReader.Api.Contracts.V1;
using DocReader.Api.Errors;
using DocReader.Api.Mapping;
using DocReader.Application.GdprDeletion;
using Microsoft.AspNetCore.Mvc;

namespace DocReader.Api.Controllers;

/// <summary>
/// Approval side of a GDPR/LGPD deletion request. Creating and listing requests hang off the document itself
/// (<c>/api/v1/documents/{id}/gdpr-delete</c>, <c>/api/v1/documents/{id}/gdpr-deletion-requests</c>); this
/// controller is for acting on one request by its own id.
/// </summary>
[ApiController]
[Route("api/v1/gdpr-deletion-requests")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError, ProblemTypes.ContentType)]
public sealed class GdprDeletionRequestsController(GdprDeletionRequestService service) : ControllerBase
{
    /// <summary>Returns one GDPR/LGPD deletion request.</summary>
    /// <param name="requestId">Identity of the request.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">The request.</response>
    /// <response code="404">There is none with this id.</response>
    [HttpGet("{requestId:guid}")]
    [ProducesResponseType(typeof(GdprDeletionRequestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    public async Task<ActionResult<GdprDeletionRequestResponse>> GetAsync(Guid requestId, CancellationToken ct) =>
        Ok(ConfigurationResponseMapper.ToResponse(await service.GetByIdAsync(requestId, ct)));

    /// <summary>Approves a PENDING GDPR/LGPD deletion request.</summary>
    /// <remarks>
    /// TODO(RBAC): restrict to the docreader-admin role once OIDC/RBAC lands (see CLAUDE.md); today this is
    /// reachable by anyone who can reach the API, like the rest of this anonymous PoC. The same request is also
    /// approved automatically by the worker after the configured window (24 hours by default) if nobody decides;
    /// that path records <c>approvedBy=system:auto-approve-24h</c> instead of an operator identity, so the two are
    /// distinguishable in the timeline and the audit log. Approving does not delete anything by itself: the worker
    /// executes the request afterwards.
    /// </remarks>
    /// <param name="requestId">Identity of the request.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">Approved.</response>
    /// <response code="404">There is none with this id.</response>
    /// <response code="409">The request is not PENDING (already approved, rejected or executed).</response>
    [HttpPost("{requestId:guid}/approve")]
    [ProducesResponseType(typeof(GdprDeletionRequestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    public async Task<ActionResult<GdprDeletionRequestResponse>> ApproveAsync(Guid requestId, CancellationToken ct)
    {
        var approved = await service.ApproveAsync(
            requestId,
            User.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            ct);

        return Ok(ConfigurationResponseMapper.ToResponse(approved));
    }

    /// <summary>Rejects a PENDING GDPR/LGPD deletion request. Nothing is deleted.</summary>
    /// <remarks>
    /// TODO(RBAC): restrict to the docreader-admin role once OIDC/RBAC lands (see CLAUDE.md); today this is
    /// reachable by anyone who can reach the API, like the rest of this anonymous PoC.
    /// </remarks>
    /// <param name="requestId">Identity of the request.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">Rejected.</response>
    /// <response code="404">There is none with this id.</response>
    /// <response code="409">The request is not PENDING (already approved, rejected or executed).</response>
    [HttpPost("{requestId:guid}/reject")]
    [ProducesResponseType(typeof(GdprDeletionRequestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound, ProblemTypes.ContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict, ProblemTypes.ContentType)]
    public async Task<ActionResult<GdprDeletionRequestResponse>> RejectAsync(Guid requestId, CancellationToken ct)
    {
        var rejected = await service.RejectAsync(
            requestId,
            User.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            ct);

        return Ok(ConfigurationResponseMapper.ToResponse(rejected));
    }
}
