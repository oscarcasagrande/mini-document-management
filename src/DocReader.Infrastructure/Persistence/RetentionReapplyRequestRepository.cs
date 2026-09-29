using DocReader.Application.Abstractions;
using DocReader.Domain.Documents;
using DocReader.Domain.Retention;
using Microsoft.EntityFrameworkCore;

namespace DocReader.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of the retention reapply requests and the batch work behind them. All SQL is
/// parameterized by the provider.
/// </summary>
public sealed class RetentionReapplyRequestRepository(DocReaderDbContext dbContext) : IRetentionReapplyRequestRepository
{
    public async Task AddAsync(RetentionReapplyRequest request, CancellationToken ct)
    {
        dbContext.RetentionReapplyRequests.Add(request);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<RetentionReapplyRequest?> FindByIdAsync(Guid id, CancellationToken ct) =>
        dbContext.RetentionReapplyRequests.FirstOrDefaultAsync(request => request.Id == id, ct);

    public Task SaveChangesAsync(CancellationToken ct) => dbContext.SaveChangesAsync(ct);

    public async Task<RetentionReapplyRequest?> ClaimNextPendingAsync(DateTimeOffset now, CancellationToken ct)
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
                    UPDATE retention_reapply_requests
                    SET status = 'RUNNING', started_at = {now}
                    WHERE id = (
                        SELECT id FROM retention_reapply_requests
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

            var request = await dbContext.RetentionReapplyRequests
                .FirstAsync(item => item.Id == claimed[0], cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return request;
        }, ct).ConfigureAwait(false);
    }

    public async Task<int> ReapplyBatchAsync(RetentionPolicy policy, int batchSize, DateTimeOffset now, CancellationToken ct)
    {
        var documentIds = await dbContext.Documents
            .AsNoTracking()
            .Where(document => document.RetentionPolicyId == policy.Id && document.RetentionDays != policy.RetentionDays)
            .OrderBy(document => document.UploadedAt)
            .ThenBy(document => document.Id)
            .Select(document => document.Id)
            .Take(batchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var updated = 0;

        foreach (var documentId in documentIds)
        {
            if (await ReapplyOneAsync(documentId, policy, now, ct).ConfigureAwait(false))
            {
                updated++;
            }
        }

        return updated;
    }

    private async Task<bool> ReapplyOneAsync(Guid documentId, RetentionPolicy policy, DateTimeOffset now, CancellationToken ct)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async cancellationToken =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            // Lock the row so a reprocess request and this reapply cannot both win.
            var locked = await dbContext.Database
                .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM documents WHERE id = {documentId} FOR UPDATE")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (locked.Count == 0)
            {
                return false;
            }

            var document = await dbContext.Documents
                .FirstAsync(candidate => candidate.Id == documentId, cancellationToken)
                .ConfigureAwait(false);

            // The document may have moved on since the batch was selected: reprocessed, or already reapplied
            // by a previous, partially-completed run. Either way there is nothing to do here.
            if (document.RetentionPolicyId != policy.Id || document.RetentionDays == policy.RetentionDays)
            {
                return false;
            }

            document.ReapplyRetention(policy);
            document.RecordProgress(
                DocumentEventTypes.RetentionPolicyReapplied,
                now,
                $"policyId={policy.Id} retentionDays={policy.RetentionDays}");

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return true;
        }, ct).ConfigureAwait(false);
    }
}
