using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using DocReader.Application.Processing;
using DocReader.Domain;
using DocReader.Domain.Processing;
using DocReader.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace DocReader.Infrastructure.Queue;

/// <summary>
/// A fila da ADR 0001: a tabela <c>processing_jobs</c> consumida com
/// <c>FOR UPDATE SKIP LOCKED</c>.
///
/// A reserva é uma única instrução, e nenhuma linha fica travada enquanto o OCR roda: é isso que
/// permite subir réplicas de worker sem coordenação externa.
///
/// Quem detém um job prova que está vivo renovando <c>locked_at</c> a cada página lida (ADR 0002). A
/// recuperação de job preso mede o silêncio desde o último heartbeat, então um documento longo que
/// segue avançando nunca é pego por outro worker. Todo desfecho (heartbeat, completar, falhar,
/// devolver) é condicionado ao número da tentativa: um worker que perdeu a reserva não consegue
/// sobrescrever o trabalho de quem a assumiu.
/// </summary>
public sealed class PostgresProcessingQueue(
    DocReaderDbContext dbContext,
    IOptions<ProcessingQueueOptions> options,
    TimeProvider timeProvider,
    ILogger<PostgresProcessingQueue> logger) : IProcessingQueue
{
    /// <summary>
    /// O SELECT interno escolhe e trava uma linha pulando as já travadas por outro worker; o UPDATE
    /// externo a marca como reservada. Uma instrução só, atômica sem transação explícita.
    /// </summary>
    private const string AcquireSql =
        """
        UPDATE processing_jobs
        SET status = 'RUNNING',
            attempt_count = attempt_count + 1,
            locked_at = @now,
            locked_by = @worker,
            started_at = COALESCE(started_at, @now),
            pages_completed = 0,
            page_count = NULL
        WHERE id = (
            SELECT id
            FROM processing_jobs
            WHERE status = 'PENDING' AND available_at <= @now
            ORDER BY created_at
            FOR UPDATE SKIP LOCKED
            LIMIT 1
        )
        RETURNING id;
        """;

    /// <summary>
    /// Devolve à fila os jobs cujo worker ficou sem dar sinal de vida, e devolve o documento ao
    /// estado QUEUED registrando o evento, tudo em uma instrução.
    /// </summary>
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

    private readonly ProcessingQueueOptions _options = options.Value;

    public async Task EnqueueAsync(Guid documentId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var job = ProcessingJob.CreateForDocument(documentId, now);

        dbContext.ProcessingJobs.Add(job);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Processing job enqueued. documentId={DocumentId} jobId={JobId}",
            documentId,
            job.Id);
    }

    public async Task<ProcessingJob?> AcquireNextAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        await ReleaseStuckJobsAsync(now, ct).ConfigureAwait(false);

        var jobId = await ExecuteScalarGuidAsync(
            AcquireSql,
            [
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now },
                new NpgsqlParameter("worker", NpgsqlDbType.Varchar) { Value = _options.WorkerName }
            ],
            ct).ConfigureAwait(false);

        if (jobId is null)
        {
            return null;
        }

        var job = await dbContext.ProcessingJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == jobId.Value, ct)
            .ConfigureAwait(false);

        if (job is not null)
        {
            logger.LogInformation(
                "Processing job acquired. jobId={JobId} documentId={DocumentId} attempt={Attempt} worker={Worker}",
                job.Id,
                job.DocumentId,
                job.AttemptCount,
                _options.WorkerName);
        }

        return job;
    }

    public async Task<bool> HeartbeatAsync(ProcessingJob job, int pagesCompleted, int pageCount, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

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

    public async Task CompleteAsync(ProcessingJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var now = timeProvider.GetUtcNow();

        var completed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE processing_jobs
             SET status = 'COMPLETED', finished_at = {now}, locked_at = NULL, locked_by = NULL,
                 error_code = NULL, error_message = NULL
             WHERE id = {job.Id} AND status = 'RUNNING' AND attempt_count = {job.AttemptCount};
             """,
            ct).ConfigureAwait(false);

        if (completed == 0)
        {
            logger.LogWarning("Complete refused: the job is not owned by this attempt. jobId={JobId}", job.Id);
            return;
        }

        logger.LogInformation("Processing job completed. jobId={JobId}", job.Id);
    }

    public async Task<JobFailureOutcome> FailAsync(ProcessingJob job, ProcessingError error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(error);

        var now = timeProvider.GetUtcNow();
        var willRetry = RetryBackoff.ShouldRetry(job.AttemptCount, error.IsTransient, _options);
        var availableAt = willRetry ? now.Add(RetryBackoff.For(job.AttemptCount, _options)) : (DateTimeOffset?)null;

        // The retrying execution strategy has to own the transaction, otherwise a retry would replay
        // only part of it.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        var applied = await strategy.ExecuteAsync(async cancellationToken =>
        {
            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            // Each attempt reads fresh state: the same context may have loaded this document earlier.
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

            // The document reflects the outcome too: the original stays consultable, and the
            // interface has to say why the reading did not come out.
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
                _options.MaxAttempts,
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

    public async Task ReleaseAsync(ProcessingJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var now = timeProvider.GetUtcNow();
        var strategy = dbContext.Database.CreateExecutionStrategy();

        var released = await strategy.ExecuteAsync(async cancellationToken =>
        {
            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            dbContext.ChangeTracker.Clear();

            // The attempt is given back: a restart is not the document's fault, so it must not eat
            // one of the three tries.
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
    }

    private async Task ReleaseStuckJobsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var threshold = now.Subtract(_options.JobLockTimeout);

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
                _options.JobLockTimeout);
        }
    }

    private async Task<Guid?> ExecuteScalarGuidAsync(
        string sql,
        NpgsqlParameter[] parameters,
        CancellationToken ct)
    {
        await dbContext.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

        try
        {
            var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddRange(parameters);

            var raw = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

            return raw is Guid id ? id : null;
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
