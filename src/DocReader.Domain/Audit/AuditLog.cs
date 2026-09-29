namespace DocReader.Domain.Audit;

/// <summary>
/// Append-only, system-wide record of a mutating or sensitive action. <see cref="Changes"/> is metadata about
/// what changed (field names, ids), never document content or a secret value.
/// </summary>
public sealed class AuditLog
{
    public const int MaxChangesLength = 2048;

    private AuditLog()
    {
    }

    public Guid Id { get; private init; }

    /// <summary>Who performed the action. Null until OIDC authentication exists: the PoC runs anonymous today.</summary>
    public string? UserId { get; private init; }

    public string Action { get; private init; } = string.Empty;

    public string ResourceType { get; private init; } = string.Empty;

    public string ResourceId { get; private init; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private init; }

    public string? IpAddress { get; private init; }

    public string? UserAgent { get; private init; }

    public string? Changes { get; private init; }

    public static AuditLog Create(
        Guid id,
        string? userId,
        string action,
        string resourceType,
        string resourceId,
        DateTimeOffset occurredAt,
        string? ipAddress,
        string? userAgent,
        string? changes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);

        return new AuditLog
        {
            Id = id,
            UserId = userId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            OccurredAt = occurredAt,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Changes = changes is { Length: > MaxChangesLength } ? changes[..MaxChangesLength] : changes
        };
    }
}
