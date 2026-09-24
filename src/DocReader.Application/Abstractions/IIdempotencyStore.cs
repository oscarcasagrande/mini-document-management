using DocReader.Domain.Idempotency;

namespace DocReader.Application.Abstractions;

/// <summary>
/// Lookup of <c>Idempotency-Key</c> records. Writing happens inside
/// <see cref="IDocumentRepository.AcceptAsync"/> so the key and the document share a transaction.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Returns the live record for the key, or null when absent or expired. Expired records are
    /// discarded so the key becomes reusable.
    /// </summary>
    Task<IdempotencyRecord?> FindLiveAsync(string key, CancellationToken ct);
}
