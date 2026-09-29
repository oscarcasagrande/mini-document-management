using DocReader.Domain.Documents;
using DocReader.Domain.StorageMigrations;

namespace DocReader.Application.Abstractions;

/// <summary>
/// Persistence of the storage migration jobs and the per-document work of moving a document to another repository.
/// Every change to a job goes through <see cref="UpdateLockedAsync"/>, under a row lock, so the worker finishing a
/// job and a caller cancelling it cannot overwrite each other.
/// </summary>
public interface IStorageMigrationJobRepository
{
    Task AddAsync(StorageMigrationJob job, CancellationToken ct);

    /// <summary>The job as it is now, not tracked; null when there is none with this id.</summary>
    Task<StorageMigrationJob?> FindByIdAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Atomically claims one pending job (oldest first) and marks it running, or returns null when there is nothing
    /// pending. Safe for several workers to call at once.
    /// </summary>
    Task<StorageMigrationJob?> ClaimNextPendingAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>The current status, read fresh from the store, or null when the job does not exist.</summary>
    Task<StorageMigrationStatus?> GetStatusAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Locks the job, applies <paramref name="change"/> to it and saves, in one transaction. Returns the changed job,
    /// or null when there is none with this id. An exception thrown by <paramref name="change"/> discards the change.
    /// </summary>
    Task<StorageMigrationJob?> UpdateLockedAsync(Guid id, Action<StorageMigrationJob> change, CancellationToken ct);

    /// <summary>
    /// Up to <paramref name="batchSize"/> ids of documents the job still has to move: stored on its source repository,
    /// matching its filter, not purged, not moved by this same job before, and not in <paramref name="excludedIds"/>.
    /// Oldest upload first.
    /// </summary>
    Task<IReadOnlyList<Guid>> FindBatchAsync(
        StorageMigrationJob job,
        int batchSize,
        IReadOnlyCollection<Guid> excludedIds,
        CancellationToken ct);

    /// <summary>
    /// Moves one document, in its own transaction: locks its row, checks it is still eligible for the job (it may have
    /// changed since the batch was selected), calls <paramref name="copyAsync"/> to copy the original to the target
    /// repository, points the document at the copy and records the move on its timeline. Returns false, doing nothing,
    /// when the document is gone or no longer eligible. An exception from <paramref name="copyAsync"/> propagates and
    /// leaves the document untouched.
    /// </summary>
    /// <param name="documentId">The document to move.</param>
    /// <param name="job">The job moving it.</param>
    /// <param name="copyAsync">Copies the original of the (locked) document to the target and returns the new storage key.</param>
    /// <param name="now">Instant recorded on the timeline.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> MigrateDocumentAsync(
        Guid documentId,
        StorageMigrationJob job,
        Func<Document, CancellationToken, Task<string>> copyAsync,
        DateTimeOffset now,
        CancellationToken ct);
}
