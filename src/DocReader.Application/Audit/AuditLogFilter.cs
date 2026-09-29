namespace DocReader.Application.Audit;

/// <param name="UserId">Exact match; null for every user.</param>
/// <param name="Action">Exact match, e.g. DOCUMENT_UPLOADED; null for every action.</param>
/// <param name="From">Inclusive lower bound of <c>occurredAt</c>, in UTC.</param>
/// <param name="To">Inclusive upper bound of <c>occurredAt</c>, in UTC.</param>
/// <param name="Page">One based page number.</param>
/// <param name="PageSize">Items per page.</param>
public sealed record AuditLogFilter(
    string? UserId,
    string? Action,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page,
    int PageSize);
