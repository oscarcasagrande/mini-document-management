using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using DocReader.Application.Processing;
using DocReader.Domain.Processing;
using DocReader.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace DocReader.Infrastructure.Queue;

/// <summary>
/// The PostgreSQL-side bookkeeping shared by both queue providers: heartbeat renewal, retry/failure
/// accounting and the stale-job sweep. <see cref="PostgresProcessingQueue"/> and
/// <c>RabbitMqProcessingQueue</c> both delegate to this instead of duplicating the SQL, so the two
/// providers cannot drift apart on what "still owns the job" means (ADR 0004).
///
/// RabbitMQ mode keeps this Postgres bookkeeping, even though RabbitMQ has its own liveness signal
/// (connection loss triggers automatic requeue): that only covers a worker that dies outright. A worker
/// that is alive, connected and simply hung never drops its TCP connection, so RabbitMQ alone would
/// never notice. The broker's own <c>x-consumer-timeout</c> is the safety net that forces that case
/// (see <see cref="RabbitMqOptions.ConsumerTimeoutMargin"/>), but this sweep gives both providers the
/// same diagnosable, in-database staleness signal and keeps <c>pages_completed</c>/<c>page_count</c>
/// progress reporting identical regardless of provider.
/// </summary>
internal sealed class ProcessingJobBookkeeping(
    DocReaderDbContext dbContext,
    TimeProvider timeProvider,
    ILogger logger)
{
    private const string ReleaseStuckSql =
        """
        WITH released AS (
            UPDATE processing_jobs
            SET status = 'PENDING',
                locked_at = NULL,
                locked_by = NULL,
                available_at = @now,
                pages_completed = 0
            WHERE status = 'RUNNING' AND locked_at < @threshold
            RETURNING document_id
        ),
        requeued AS (
            UPDATE documents
            SET status = 'QUEUED'
            WHERE id IN (SELECT document_id FROM released)
            RETURNING id
        )
        INSERT INTO document_events (id, document_id, event_type, stage, details, occurred_at)
        SELECT gen_random_uuid(), id, 'RETRY_SCHEDULED', 'QUEUED', 'STUCK_JOB_RECOVERED', @now
        FROM requeued;
        """;

    public async Task<bool> HeartbeatAsync(ProcessingJob job, int pagesCompleted, int pageCount, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        var renewed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE processing_jobs
             SET locked_at = {now}, pages_completed = {pagesCompleted}, page_count = {pageCount}
             WHERE id = {job.Id} AND status = 'RUNNING' AND attempt_count = {job.AttemptCount};
             """,
            ct).ConfigureAwait(false);

        if (renewed == 0)
        {
            logger.LogWarning(
                "Heartbeat refused: the job is no longer owned by this attempt. jobId={JobId} attempt={Attempt}",
                job.Id,
                job.AttemptCount);
        }

        return renewed > 0;
    }

    public async Task<JobFailureOutcome> FailAsync(ProcessingJob job, ProcessingError error, ProcessingQueueOptions options, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var willRetry = RetryBackoff.ShouldRetry(job.AttemptCount, error.IsTransient, options);
        var availableAt = willRetry ? now.Add(RetryBackoff.For(job.AttemptCount, options)) : (DateTimeOffset?)null;

        var strategy = dbContext.Database.CreateExecutionStrategy();

        var applied = await strategy.ExecuteAsync(async cancellationToken =>
        {
            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            dbContext.ChangeTracker.Clear();

            var affected = willRetry
                ? await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE processing_jobs
                     SET status = 'PENDING', available_at = {availableAt}, locked_at = NULL, locked_by = NULL,
                         error_code = {error.Code}, error_message = {error.Message}
                     WHERE id = {job.Id} AND status = 'RUNNING' AND attempt_count = {job.AttemptCount};
                     """,
                    cancellationToken).ConfigureAwait(false)
                : await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE processing_jobs
                     SET status = 'FAILED', finished_at = {now}, locked_at = NULL, locked_by = NULL,
                         error_code = {error.Code}, error_message = {error.Message}
                     WHERE id = {job.Id} AND status = 'RUNNING' AND attempt_count = {job.AttemptCount};
                     """,
                    cancellationToken).ConfigureAwait(false);

            if (affected == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            var document = await dbContext.Documents
                .FirstOrDefaultAsync(candidate => candidate.Id == job.DocumentId, cancellationToken)
                .ConfigureAwait(false);

            if (document is not null)
            {
                if (willRetry)
                {
                    document.MarkRetryScheduled(error.Code, error.Message, now);
                }
                else
                {
                    document.MarkFailed(error.Code, error.Message, now);
                    await WebhookOutbox.EnqueueAsync(dbContext, document, now, cancellationToken).ConfigureAwait(false);
                }

                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);

        if (!applied)
        {
            logger.LogWarning(
                "Fail refused: the job is not owned by this attempt. jobId={JobId} attempt={Attempt} errorCode={ErrorCode}",
                job.Id,
                job.AttemptCount,
                error.Code);
            return new JobFailureOutcome(Applied: false, WillRetry: false, AvailableAt: null);
        }

        if (willRetry)
        {
            logger.LogWarning(
                "Processing job scheduled for retry. jobId={JobId} attempt={Attempt} of {MaxAttempts} errorCode={ErrorCode} availableAt={AvailableAt}",
                job.Id,
                job.AttemptCount,
                options.MaxAttempts,
                error.Code,
                availableAt);
        }
        else
        {
            logger.LogError(
                "Processing job failed definitively. jobId={JobId} documentId={DocumentId} attempt={Attempt} errorCode={ErrorCode}",
                job.Id,
                job.DocumentId,
                job.AttemptCount,
                error.Code);
        }

        return new JobFailureOutcome(Applied: true, willRetry, availableAt);
    }

    public async Task<bool> ReleaseAsync(ProcessingJob job, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var strategy = dbContext.Database.CreateExecutionStrategy();

        var released = await strategy.ExecuteAsync(async cancellationToken =>
        {
            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            dbContext.ChangeTracker.Clear();

            var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE processing_jobs
                 SET status = 'PENDING', available_at = {now}, locked_at = NULL, locked_by = NULL,
                     attempt_count = attempt_count - 1, pages_completed = 0
                 WHERE id = {job.Id} AND status = 'RUNNING' AND attempt_count = {job.AttemptCount};
                 """,
                cancellationToken).ConfigureAwait(false);

            if (affected == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            var document = await dbContext.Documents
                .FirstOrDefaultAsync(candidate => candidate.Id == job.DocumentId, cancellationToken)
                .ConfigureAwait(false);

            if (document is not null)
            {
                document.MarkRequeued(now, "WORKER_STOPPED");
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);

        if (released)
        {
            logger.LogInformation("Processing job released back to the queue. jobId={JobId}", job.Id);
        }

        return released;
    }

    /// <summary>
    /// Returns jobs whose worker went silent past <paramref name="lockTimeout"/> to PENDING. Called by
    /// the Postgres provider before every poll, and by the RabbitMQ provider before every acquire, for
    /// the "hung consumer" case documented on this type.
    /// </summary>
    public async Task ReleaseStuckJobsAsync(TimeSpan lockTimeout, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var threshold = now.Subtract(lockTimeout);

        var released = await dbContext.Database.ExecuteSqlRawAsync(
            ReleaseStuckSql,
            [
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now },
                new NpgsqlParameter("threshold", NpgsqlDbType.TimestampTz) { Value = threshold }
            ],
            ct).ConfigureAwait(false);

        if (released > 0)
        {
            logger.LogWarning(
                "Released {Count} stuck processing job(s) back to pending: no heartbeat within {LockTimeout}.",
                released,
                lockTimeout);
        }
    }
}
