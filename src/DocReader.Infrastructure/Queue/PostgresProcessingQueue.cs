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

    private readonly ProcessingQueueOptions _options = options.Value;
    private readonly ProcessingJobBookkeeping _bookkeeping = new(dbContext, timeProvider, logger);

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

        await _bookkeeping.ReleaseStuckJobsAsync(_options.JobLockTimeout, ct).ConfigureAwait(false);

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

    public Task<bool> HeartbeatAsync(ProcessingJob job, int pagesCompleted, int pageCount, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        return _bookkeeping.HeartbeatAsync(job, pagesCompleted, pageCount, ct);
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

    public Task<JobFailureOutcome> FailAsync(ProcessingJob job, ProcessingError error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(error);

        return _bookkeeping.FailAsync(job, error, _options, ct);
    }

    public Task ReleaseAsync(ProcessingJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        return _bookkeeping.ReleaseAsync(job, ct);
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
