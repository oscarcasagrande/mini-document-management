using DocReader.Domain.Backup;

namespace DocReader.Application.Abstractions;

/// <summary>
/// Persistence of the backup jobs. Finds by id return tracked entities, so a change is saved with
/// <see cref="SaveChangesAsync"/>.
/// </summary>
public interface IBackupJobRepository
{
    Task AddAsync(BackupJob job, CancellationToken ct);

    Task<BackupJob?> FindByIdAsync(Guid id, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>
    /// Atomically claims one pending job (oldest first) and marks it running, or returns null when there is nothing
    /// pending. Safe for several workers to call at once.
    /// </summary>
    Task<BackupJob?> ClaimNextPendingAsync(DateTimeOffset now, CancellationToken ct);
}
