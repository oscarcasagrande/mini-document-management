using DocReader.Api.Contracts.V1;
using DocReader.Domain.Audit;

namespace DocReader.Api.Mapping;

/// <summary>Maps <see cref="AuditLog"/> to its contract.</summary>
public static class AuditResponseMapper
{
    public static AuditLogResponse ToResponse(AuditLog entry) => new(
        entry.Id,
        entry.UserId,
        entry.Action,
        entry.ResourceType,
        entry.ResourceId,
        entry.OccurredAt,
        entry.IpAddress,
        entry.UserAgent,
        entry.Changes);
}
