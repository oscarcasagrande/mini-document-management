using DocReader.Application.Abstractions;
using DocReader.Domain.Backup;
using Microsoft.EntityFrameworkCore;

namespace DocReader.Infrastructure.Persistence;

/// <summary>EF Core implementation of the backup jobs. All SQL is parameterized by the provider.</summary>
public sealed class BackupJobRepository(DocReaderDbContext dbContext) : IBackupJobRepository
{
    public async Task AddAsync(BackupJob job, CancellationToken ct)
    {
        dbContext.BackupJobs.Add(job);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<BackupJob?> FindByIdAsync(Guid id, CancellationToken ct) =>
        dbContext.BackupJobs.FirstOrDefaultAsync(job => job.Id == id, ct);

    public Task SaveChangesAsync(CancellationToken ct) => dbContext.SaveChangesAsync(ct);

    public async Task<BackupJob?> ClaimNextPendingAsync(DateTimeOffset now, CancellationToken ct)
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
                    UPDATE backup_jobs
                    SET status = 'RUNNING', started_at = {now}
                    WHERE id = (
                        SELECT id FROM backup_jobs
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

            var job = await dbContext.BackupJobs
                .FirstAsync(item => item.Id == claimed[0], cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return job;
        }, ct).ConfigureAwait(false);
    }
}
