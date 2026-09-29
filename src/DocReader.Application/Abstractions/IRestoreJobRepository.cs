using DocReader.Domain.Backup;

namespace DocReader.Application.Abstractions;

/// <summary>
/// Persistence of the restore jobs. Finds by id return tracked entities, so a change is saved with
/// <see cref="SaveChangesAsync"/>.
/// </summary>
public interface IRestoreJobRepository
{
    /// <exception cref="Errors.ResourceConflictException">Another restore is already pending or running.</exception>
    Task AddAsync(RestoreJob job, CancellationToken ct);

    Task<RestoreJob?> FindByIdAsync(Guid id, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>Whether a restore is pending or running. At most one can be, at any time.</summary>
    Task<bool> HasActiveAsync(CancellationToken ct);

    /// <summary>Ids of the jobs left in the running state, for recovery after a worker restart.</summary>
    Task<IReadOnlyList<Guid>> ListRunningAsync(CancellationToken ct);

    /// <summary>
    /// Atomically claims one pending job (oldest first) and marks it running, or returns null when there is nothing
    /// pending. Safe for several workers to call at once.
    /// </summary>
    Task<RestoreJob?> ClaimNextPendingAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Drops every entity loaded so far in this unit of work. Called right after a restore replaced the database
    /// underneath it, so nothing loaded before (a storage repository, the job itself) is read back stale or written over
    /// the restored rows.
    /// </summary>
    void ForgetLoadedState();
}
