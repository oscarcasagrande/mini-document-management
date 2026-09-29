using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace DocReader.Api.Contracts.V1;

/// <summary>One recorded action: who did what, to what, and when. <c>changes</c> is metadata about what an update touched, never a value.</summary>
/// <param name="Id">Identity of the entry.</param>
/// <param name="UserId">Who performed the action; null until OIDC authentication exists, because the PoC runs anonymous today.</param>
/// <param name="Action">Action name, e.g. DOCUMENT_UPLOADED, RETENTION_POLICY_UPDATED.</param>
/// <param name="ResourceType">Kind of resource affected, e.g. Document, RetentionPolicy.</param>
/// <param name="ResourceId">Identity of the affected resource, as a string (most are a Guid).</param>
/// <param name="OccurredAt">When the action happened, in UTC.</param>
/// <param name="IpAddress">Caller's IP address, if known.</param>
/// <param name="UserAgent">Caller's User-Agent header, if it sent one.</param>
/// <param name="Changes">For an update, the request's property names that carried a value; null for a create, a delete, or an update that touched nothing. Never a value.</param>
public sealed record AuditLogResponse(
    Guid Id,
    string? UserId,
    string Action,
    string ResourceType,
    string ResourceId,
    DateTimeOffset OccurredAt,
    string? IpAddress,
    string? UserAgent,
    string? Changes);

/// <summary>Filters and paging of the audit log listing endpoint. Every filter is optional and they combine with AND.</summary>
public sealed class AuditLogListRequest
{
    /// <summary>Exact match on who performed the action.</summary>
    [FromQuery(Name = "userId")]
    [MaxLength(256)]
    public string? UserId { get; init; }

    /// <summary>Exact match on the action name, e.g. DOCUMENT_UPLOADED.</summary>
    [FromQuery(Name = "action")]
    [MaxLength(64)]
    public string? Action { get; init; }

    /// <summary>Lower bound of <c>occurredAt</c>, inclusive, ISO 8601 in UTC.</summary>
    [FromQuery(Name = "from")]
    public DateTimeOffset? From { get; init; }

    /// <summary>Upper bound of <c>occurredAt</c>, inclusive, ISO 8601 in UTC.</summary>
    [FromQuery(Name = "to")]
    public DateTimeOffset? To { get; init; }

    /// <summary>One based page number. Defaults to 1.</summary>
    [FromQuery(Name = "page")]
    [Range(1, int.MaxValue)]
    [DefaultValue(1)]
    public int Page { get; init; } = 1;

    /// <summary>Items per page. Defaults to the configured page size and is capped by the maximum.</summary>
    [FromQuery(Name = "pageSize")]
    [Range(1, 500)]
    public int? PageSize { get; init; }
}
