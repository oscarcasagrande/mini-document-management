using DocReader.Api.Contracts.V1;
using DocReader.Api.Errors;
using DocReader.Api.Mapping;
using DocReader.Application.Audit;
using DocReader.Application.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DocReader.Api.Controllers;

/// <summary>
/// Read-only audit trail: who did what, to what, and when. <c>changes</c> is metadata about what an update
/// touched, never a document value or a secret.
/// </summary>
[ApiController]
[Route("api/v1/audit-logs")]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError, ProblemTypes.ContentType)]
public sealed class AuditLogsController(AuditLogService service, IOptions<PagingOptions> pagingOptions) : ControllerBase
{
    /// <summary>Lists recorded actions, newest first.</summary>
    /// <remarks>
    /// TODO(RBAC): restrict to the docreader-admin role once OIDC/RBAC lands (see CLAUDE.md); today this is
    /// reachable by anyone who can reach the API, like the rest of this anonymous PoC. Nothing is written here:
    /// this endpoint only reads <c>audit_logs</c>.
    /// </remarks>
    /// <param name="request">Filters (userId, action, occurredAt date range) and paging.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <response code="200">One page of audit entries.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<AuditLogResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResponse<AuditLogResponse>>> ListAsync(
        [FromQuery] AuditLogListRequest request,
        CancellationToken ct)
    {
        var paging = pagingOptions.Value;
        var pageSize = Math.Clamp(request.PageSize ?? paging.DefaultPageSize, 1, paging.MaxPageSize);

        var page = await service.ListAsync(
            new AuditLogFilter(request.UserId, request.Action, request.From, request.To, Math.Max(1, request.Page), pageSize),
            ct);

        return Ok(ConfigurationResponseMapper.ToPage(page, AuditResponseMapper.ToResponse));
    }
}
