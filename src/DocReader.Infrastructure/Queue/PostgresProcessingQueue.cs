using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using DocReader.Application.Processing;
using DocReader.Domain;
using DocReader.Domain.Documents;
using DocReader.Domain.Processing;
using DocReader.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace DocReader.Infrastructure.Queue;

/// <summary>
/// A fila da ADR 0001: a tabela <c>processing_jobs</c> consumida com
/// <c>FOR UPDATE SKIP LOCKED</c>.
///
/// A transação da reserva cobre apenas a reserva, nunca o processamento: nenhuma linha fica travada
/// enquanto o OCR roda, o que é o que permite subir réplicas de worker sem coordenação externa.
/// </summary>
public sealed class PostgresProcessingQueue(
    DocReaderDbContext dbContext,
    IOptions<ProcessingQueueOptions> options,
    TimeProvider timeProvider,
    ILogger<PostgresProcessingQueue> logger) : IProcessingQueue
{
    /// <summary>
    /// O SELECT interno escolhe e trava uma linha pulando as já travadas por outro worker; o UPDATE
    /// externo a marca como reservada. Tudo em uma ida ao banco.
    /// </summary>
    private const string AcquireSql =
        """
        UPDATE processing_jobs
        SET status = 'RUNNING',
            attempt_count = attempt_count + 1,
            locked_at = @now,
            locked_by = @worker,
            started_at = COALESCE(started_at, @now)
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

    private const string ReleaseStuckSql =
        """
        UPDATE processing_jobs
        SET status = 'PENDING',
            locked_at = NULL,
            locked_by = NULL,
            available_at = @now
        WHERE status = 'RUNNING' AND locked_at < @threshold;
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

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(ct)
            .ConfigureAwait(false);

        var jobId = await ExecuteScalarGuidAsync(
            AcquireSql,
            [
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now },
                new NpgsqlParameter("worker", NpgsqlDbType.Varchar) { Value = _options.WorkerName }
            ],
            ct).ConfigureAwait(false);

        if (jobId is null)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return null;
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

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

    public async Task CompleteAsync(Guid jobId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE processing_jobs
             SET status = 'COMPLETED', finished_at = {now}, locked_at = NULL, locked_by = NULL,
                 error_code = NULL, error_message = NULL
             WHERE id = {jobId};
             """,
            ct).ConfigureAwait(false);

        logger.LogInformation("Processing job completed. jobId={JobId}", jobId);
    }

    public async Task FailAsync(Guid jobId, ProcessingError error, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        var job = await dbContext.ProcessingJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == jobId, ct)
            .ConfigureAwait(false);

        if (job is null)
        {
            logger.LogWarning("Tried to fail a job that no longer exists. jobId={JobId}", jobId);
            return;
        }

        if (RetryBackoff.ShouldRetry(job.AttemptCount, error.IsTransient, _options))
        {
            var availableAt = now.Add(RetryBackoff.For(job.AttemptCount, _options));

            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE processing_jobs
                 SET status = 'PENDING', available_at = {availableAt}, locked_at = NULL, locked_by = NULL,
                     error_code = {error.Code}, error_message = {error.Message}
                 WHERE id = {jobId};
                 """,
                ct).ConfigureAwait(false);

            logger.LogWarning(
                "Processing job scheduled for retry. jobId={JobId} attempt={Attempt} of {MaxAttempts} errorCode={ErrorCode} availableAt={AvailableAt}",
                jobId,
                job.AttemptCount,
                _options.MaxAttempts,
                error.Code,
                availableAt);
            return;
        }

        var failedStatus = EnumNaming.ToUpperSnakeCase(DocumentStatus.Failed);

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE processing_jobs
             SET status = 'FAILED', finished_at = {now}, locked_at = NULL, locked_by = NULL,
                 error_code = {error.Code}, error_message = {error.Message}
             WHERE id = {jobId};
             """,
            ct).ConfigureAwait(false);

        // O documento também precisa refletir a falha: o original continua consultável, mas a
        // interface tem de mostrar por que a leitura não saiu.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE documents
             SET status = {failedStatus}, last_error_code = {error.Code}, last_error_message = {error.Message}
             WHERE id = {job.DocumentId};
             """,
            ct).ConfigureAwait(false);

        logger.LogError(
            "Processing job failed definitively. jobId={JobId} documentId={DocumentId} attempt={Attempt} errorCode={ErrorCode}",
            jobId,
            job.DocumentId,
            job.AttemptCount,
            error.Code);
    }

    /// <summary>
    /// Devolve à fila os jobs cujo worker morreu no meio do processamento. É isto que faz um
    /// reinício de contêiner não perder trabalho (RF-007).
    /// </summary>
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
                "Released {Count} stuck processing job(s) back to pending. lockTimeout={LockTimeout}",
                released,
                _options.JobLockTimeout);
        }
    }

    private async Task<Guid?> ExecuteScalarGuidAsync(
        string sql,
        NpgsqlParameter[] parameters,
        CancellationToken ct)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        command.Parameters.AddRange(parameters);

        var raw = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return raw is Guid id ? id : null;
    }
}
