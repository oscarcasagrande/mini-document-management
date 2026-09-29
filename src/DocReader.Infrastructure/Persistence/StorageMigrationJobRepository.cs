using DocReader.Application.Abstractions;
using DocReader.Domain.Documents;
using DocReader.Domain.StorageMigrations;
using Microsoft.EntityFrameworkCore;

namespace DocReader.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of the storage migration jobs and the per-document work behind them. All SQL is
/// parameterized by the provider.
/// </summary>
public sealed class StorageMigrationJobRepository(DocReaderDbContext dbContext) : IStorageMigrationJobRepository
{
    public async Task AddAsync(StorageMigrationJob job, CancellationToken ct)
    {
        dbContext.StorageMigrationJobs.Add(job);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<StorageMigrationJob?> FindByIdAsync(Guid id, CancellationToken ct) =>
        dbContext.StorageMigrationJobs.AsNoTracking().FirstOrDefaultAsync(job => job.Id == id, ct);

    public async Task<StorageMigrationJob?> ClaimNextPendingAsync(DateTimeOffset now, CancellationToken ct)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async cancellationToken =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            var claimed = await dbContext.Database
                .SqlQuery<Guid>(
                    $"""
                    UPDATE storage_migration_jobs
                    SET status = 'RUNNING', started_at = {now}
                    WHERE id = (
                        SELECT id FROM storage_migration_jobs
                        WHERE status = 'PENDING'
                        ORDER BY requested_at
                        LIMIT 1
                        FOR UPDATE SKIP LOCKED
                    )
                    RETURNING id AS "Value"
                    """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (claimed.Count == 0)
            {
                return null;
            }

            var job = await dbContext.StorageMigrationJobs
                .AsNoTracking()
                .FirstAsync(item => item.Id == claimed[0], cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return job;
        }, ct).ConfigureAwait(false);
    }

    public Task<StorageMigrationStatus?> GetStatusAsync(Guid id, CancellationToken ct) =>
        dbContext.StorageMigrationJobs
            .AsNoTracking()
            .Where(job => job.Id == id)
            .Select(job => (StorageMigrationStatus?)job.Status)
            .FirstOrDefaultAsync(ct);

    public async Task<StorageMigrationJob?> UpdateLockedAsync(Guid id, Action<StorageMigrationJob> change, CancellationToken ct)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async cancellationToken =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            // Waits for (and then excludes) whoever else is changing the job: the worker finishing it or a caller cancelling it.
            var locked = await dbContext.Database
                .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM storage_migration_jobs WHERE id = {id} FOR UPDATE")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (locked.Count == 0)
            {
                return null;
            }

            var job = await dbContext.StorageMigrationJobs
                .FirstAsync(candidate => candidate.Id == id, cancellationToken)
                .ConfigureAwait(false);

            change(job);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            dbContext.Entry(job).State = EntityState.Detached;

            return job;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> FindBatchAsync(
        StorageMigrationJob job,
        int batchSize,
        IReadOnlyCollection<Guid> excludedIds,
        CancellationToken ct)
    {
        var excluded = excludedIds.ToArray();

        return await Eligible(job)
            .AsNoTracking()
            .Where(document => !excluded.Contains(document.Id))
            .OrderBy(document => document.UploadedAt)
            .ThenBy(document => document.Id)
            .Select(document => document.Id)
            .Take(batchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> MigrateDocumentAsync(
        Guid documentId,
        StorageMigrationJob job,
        Func<Document, CancellationToken, Task<string>> copyAsync,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async cancellationToken =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            // Lock the row so a reprocess, a purge or a second migration cannot change it while the file is copied.
            var locked = await dbContext.Database
                .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM documents WHERE id = {documentId} FOR UPDATE")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (locked.Count == 0)
            {
                return false;
            }

            // The document may have moved on since the batch was selected: purged, moved by another job, or moved by a
            // previous, partially-completed run of this one. Either way there is nothing to do here.
            var stillEligible = await Eligible(job)
                .AnyAsync(document => document.Id == documentId, cancellationToken)
                .ConfigureAwait(false);

            if (!stillEligible)
            {
                return false;
            }

            var document = await dbContext.Documents
                .FirstAsync(candidate => candidate.Id == documentId, cancellationToken)
                .ConfigureAwait(false);

            var targetStorageKey = await copyAsync(document, cancellationToken).ConfigureAwait(false);

            document.MigrateStorageRepository(job.TargetRepositoryId, targetStorageKey, now, job.Id);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            dbContext.ChangeTracker.Clear();

            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The documents <paramref name="job"/> still has to move. One definition for selecting a batch and re-checking a locked row.</summary>
    private IQueryable<Document> Eligible(StorageMigrationJob job)
    {
        var sourceRepositoryId = job.SourceRepositoryId;
        var marker = StorageMigrationEventDetails.JobMarker(job.Id);

        var query = dbContext.Documents.Where(document =>
            document.StorageRepositoryId == sourceRepositoryId
            && document.Status != DocumentStatus.Purged
            && !document.Events.Any(entry =>
                entry.EventType == DocumentEventTypes.StorageMigrated
                && entry.Details != null
                && entry.Details.Contains(marker)));

        if (job.FilterDocumentType is { } documentType)
        {
            query = query.Where(document => document.DetectedDocumentType == documentType);
        }

        if (job.FilterProductServiceId is { } productServiceId)
        {
            query = query.Where(document => document.ProductServiceId == productServiceId);
        }

        if (job.FilterUploadedFrom is { } uploadedFrom)
        {
            query = query.Where(document => document.UploadedAt >= uploadedFrom);
        }

        if (job.FilterUploadedTo is { } uploadedTo)
        {
            query = query.Where(document => document.UploadedAt <= uploadedTo);
        }

        return query;
    }
}
