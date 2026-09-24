namespace DocReader.Application.Options;

/// <summary>
/// Retention of <c>Idempotency-Key</c> records.
/// </summary>
public sealed class IdempotencyOptions
{
    public const string SectionName = "DocReader:Idempotency";

    /// <summary>How long a key stays bound to its file hash and response. Default is 24 hours.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Maximum accepted length of the header value.</summary>
    public int MaxKeyLength { get; set; } = 200;
}
