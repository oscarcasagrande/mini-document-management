namespace DocReader.Domain.Idempotency;

/// <summary>
/// Replay record for <c>Idempotency-Key</c>. The scope of idempotency is the pair
/// key + SHA-256 of the received file, as specified in PRD section 16.
/// </summary>
public sealed class IdempotencyRecord
{
    public string Key { get; init; } = string.Empty;

    public string FileSha256 { get; init; } = string.Empty;

    public Guid DocumentId { get; init; }

    public int ResponseStatus { get; init; }

    /// <summary>Serialized response body replayed on a repeated request.</summary>
    public string ResponseBody { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;
}
