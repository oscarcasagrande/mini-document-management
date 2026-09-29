using DocReader.Domain.Audit;

namespace DocReader.Application.Audit;

/// <summary>
/// Records who did what, to what, and when — never the content.
/// </summary>
public sealed class AuditLogService(IAuditLogStore store, TimeProvider timeProvider)
{
    /// <summary>
    /// <paramref name="userId"/> is whatever identity the caller had at hand; today that is almost always null,
    /// because the PoC has no authentication yet.
    /// </summary>
    public Task RecordAsync(
        string? userId,
        string action,
        string resourceType,
        string resourceId,
        string? ipAddress,
        string? userAgent,
        string? changes,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var entry = AuditLog.Create(Guid.CreateVersion7(now), userId, action, resourceType, resourceId, now, ipAddress, userAgent, changes);

        return store.AddAsync(entry, ct);
    }
}
