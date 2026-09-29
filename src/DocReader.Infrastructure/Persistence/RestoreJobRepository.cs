using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using DocReader.Domain.Backup;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocReader.Infrastructure.Persistence;

/// <summary>EF Core implementation of the restore jobs. All SQL is parameterized by the provider.</summary>
public sealed class RestoreJobRepository(DocReaderDbContext dbContext) : IRestoreJobRepository
{
    /// <summary>Unique partial index of the migration: at most one pending or running restore.</summary>
    public const string SingleActiveIndexName = "ux_restore_jobs_single_active";

    public async Task AddAsync(RestoreJob job, CancellationToken ct)
    {
        dbContext.RestoreJobs.Add(job);

        try
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: SingleActiveIndexName
        })
        {
            dbContext.Entry(job).State = EntityState.Detached;

            throw new ResourceConflictException(
                "RESTORE_ALREADY_IN_PROGRESS",
                "Another restore is pending or running. Wait for it to finish before starting a new one.");
        }
    }

    public Task<RestoreJob?> FindByIdAsync(Guid id, CancellationToken ct) =>
        dbContext.RestoreJobs.FirstOrDefaultAsync(job => job.Id == id, ct);

    public Task SaveChangesAsync(CancellationToken ct) => dbContext.SaveChangesAsync(ct);

    public Task<bool> HasActiveAsync(CancellationToken ct) =>
        dbContext.RestoreJobs.AnyAsync(job => job.Status == RestoreJobStatus.Pending || job.Status == RestoreJobStatus.Running, ct);

    public async Task<IReadOnlyList<Guid>> ListRunningAsync(CancellationToken ct) =>
        await dbContext.RestoreJobs
            .AsNoTracking()
            .Where(job => job.Status == RestoreJobStatus.Running)
            .Select(job => job.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<RestoreJob?> ClaimNextPendingAsync(DateTimeOffset now, CancellationToken ct)
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
                    UPDATE restore_jobs
                    SET status = 'RUNNING', started_at = {now}
                    WHERE id = (
                        SELECT id FROM restore_jobs
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

            var job = await dbContext.RestoreJobs
                .FirstAsync(item => item.Id == claimed[0], cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return job;
        }, ct).ConfigureAwait(false);
    }

    public void ForgetLoadedState() => dbContext.ChangeTracker.Clear();
}
