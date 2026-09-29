using DocReader.Domain.Retention;

namespace DocReader.Application.Abstractions;

/// <summary>
/// Persistence of the retention reapply requests and the batch work of recalculating <c>expiresAt</c> on the
/// documents a policy covers. Finds by id return tracked entities, so a change is saved with
/// <see cref="SaveChangesAsync"/>.
/// </summary>
public interface IRetentionReapplyRequestRepository
{
    Task AddAsync(RetentionReapplyRequest request, CancellationToken ct);

    Task<RetentionReapplyRequest?> FindByIdAsync(Guid id, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>
    /// Atomically claims one pending request (oldest first) and marks it running, or returns null when there is
    /// nothing pending. Safe for several workers to call at once.
    /// </summary>
    Task<RetentionReapplyRequest?> ClaimNextPendingAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Recalculates <c>expiresAt</c> on up to <paramref name="batchSize"/> documents that still carry
    /// <paramref name="policy"/> under a different number of days, and records a
    /// <see cref="Domain.Documents.DocumentEventTypes.RetentionPolicyReapplied"/> event on each. Idempotent: a
    /// document whose retention already matches the policy is skipped, so calling this again after a partial run
    /// only touches what is left. Returns how many documents this call updated; zero means nothing left to do.
    /// </summary>
    Task<int> ReapplyBatchAsync(RetentionPolicy policy, int batchSize, DateTimeOffset now, CancellationToken ct);
}
