using System.Threading.Channels;
using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using DocReader.Domain.Processing;
using DocReader.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocReader.Infrastructure.Queue.RabbitMq;

/// <summary>
/// RabbitMQ-backed <see cref="IProcessingQueue"/> (ADR 0004, alternative to <see cref="PostgresProcessingQueue"/>
/// / ADR 0001). Heartbeat, retry/failure accounting and the stale-job sweep stay in PostgreSQL through
/// the shared <see cref="ProcessingJobBookkeeping"/>: only which broker delivery a job corresponds to,
/// and settling that delivery, is specific to this provider.
///
/// Registered <c>Scoped</c>, one instance per job iteration (see <c>ProcessingWorker</c>): the delivery
/// this instance is holding (<see cref="_deliveryTag"/>) is an instance field, safe because
/// <see cref="AcquireNextAsync"/> and the later <see cref="CompleteAsync"/>/<see cref="FailAsync"/>/
/// <see cref="ReleaseAsync"/> calls for the same job all run against the very same scoped instance.
/// </summary>
public sealed class RabbitMqProcessingQueue(
    DocReaderDbContext dbContext,
    RabbitMqJobBridge bridge,
    IOptions<ProcessingQueueOptions> options,
    TimeProvider timeProvider,
    ILogger<RabbitMqProcessingQueue> logger) : IProcessingQueue
{
    private readonly ProcessingQueueOptions _options = options.Value;
    private readonly ProcessingJobBookkeeping _bookkeeping = new(dbContext, timeProvider, logger);

    private ulong? _deliveryTag;
    private Guid? _deliveryJobId;

    /// <summary>
    /// Correct per the interface contract but not part of the real request path: uploading a document
    /// writes the <c>processing_jobs</c> row <em>and</em> the transactional outbox row directly in
    /// <c>DocumentRepository.AcceptAsync</c>/its reprocess counterpart, in the same
    /// <c>SaveChangesAsync</c>; <c>RabbitMqOutboxPublisher</c> is what actually publishes to the broker.
    /// This method only does the PostgreSQL half, honestly, and does not publish anything itself - same
    /// standard as <see cref="PostgresProcessingQueue.CompleteAsync"/> staying correct despite being
    /// unreachable today.
    /// </summary>
    public async Task EnqueueAsync(Guid documentId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var job = ProcessingJob.CreateForDocument(documentId, now);

        dbContext.ProcessingJobs.Add(job);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Processing job row written (no outbox row, no publish: see xml doc). documentId={DocumentId} jobId={JobId}",
            documentId,
            job.Id);
    }

    public async Task<ProcessingJob?> AcquireNextAsync(CancellationToken ct)
    {
        while (true)
        {
            RabbitMqDelivery delivery;

            try
            {
                delivery = await bridge.Deliveries.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (ChannelClosedException)
            {
                return null;
            }

            await _bookkeeping.ReleaseStuckJobsAsync(_options.JobLockTimeout, ct).ConfigureAwait(false);

            var now = timeProvider.GetUtcNow();
            var acquired = await AcquireTargetedAsync(delivery.JobId, now, ct).ConfigureAwait(false);

            if (!acquired)
            {
                // The targeted UPDATE only touches a row that is still PENDING. Zero rows affected means
                // this delivery is a duplicate (redelivery after a crash before the earlier ack landed)
                // or a job another attempt already settled: not new work. Ack it so the broker drops it,
                // and keep listening instead of surfacing it to ProcessingWorker as a fresh job. This is
                // the idempotency guard for "a repeated message must not create a second execution".
                logger.LogInformation(
                    "Duplicate or stale RabbitMQ delivery acked without reprocessing. jobId={JobId}",
                    delivery.JobId);

                await bridge.AckAsync(delivery.DeliveryTag, ct).ConfigureAwait(false);
                continue;
            }

            var job = await dbContext.ProcessingJobs
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == delivery.JobId, ct)
                .ConfigureAwait(false);

            if (job is null)
            {
                // The row disappeared under us between the UPDATE and this SELECT: nothing deletes
                // processing_jobs today, so this should not happen, but there is nothing to process
                // either way. Ack and move on rather than throw out of the acquire loop.
                logger.LogWarning("Acquired job row not found right after the targeted update. jobId={JobId}", delivery.JobId);
                await bridge.AckAsync(delivery.DeliveryTag, ct).ConfigureAwait(false);
                continue;
            }

            _deliveryTag = delivery.DeliveryTag;
            _deliveryJobId = delivery.JobId;

            logger.LogInformation(
                "Processing job acquired via RabbitMQ. jobId={JobId} documentId={DocumentId} attempt={Attempt} worker={Worker}",
                job.Id,
                job.DocumentId,
                job.AttemptCount,
                _options.WorkerName);

            return job;
        }
    }

    /// <summary>
    /// A single-row, targeted reservation: unlike the Postgres provider's <c>SKIP LOCKED ... LIMIT 1</c>
    /// scan (built to pick one of many pending candidates), the broker already told us exactly which job
    /// this delivery is for, so this only needs to claim that one row if it is still PENDING.
    /// </summary>
    private async Task<bool> AcquireTargetedAsync(Guid jobId, DateTimeOffset now, CancellationToken ct)
    {
        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE processing_jobs
             SET status = 'RUNNING',
                 attempt_count = attempt_count + 1,
                 locked_at = {now},
                 locked_by = {_options.WorkerName},
                 started_at = COALESCE(started_at, {now}),
                 pages_completed = 0,
                 page_count = NULL
             WHERE id = {jobId} AND status = 'PENDING';
             """,
            ct).ConfigureAwait(false);

        return affected > 0;
    }

    public Task<bool> HeartbeatAsync(ProcessingJob job, int pagesCompleted, int pageCount, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        return _bookkeeping.HeartbeatAsync(job, pagesCompleted, pageCount, ct);
    }

    public async Task CompleteAsync(ProcessingJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        // The actual completion write already happened in DocumentProcessingStore.CompleteAsync, in the
        // same transaction as the extraction (DocumentProcessor never calls this interface method on the
        // success path). This only settles the broker delivery. A cleared delivery here means FailAsync
        // or ReleaseAsync already settled it earlier for this same job (this scoped instance is reused
        // for the whole attempt), so this is a safe no-op - ProcessingWorker calls this unconditionally
        // after ProcessAsync returns, and ProcessAsync itself never rethrows on failure/cancellation.
        if (_deliveryTag is not { } tag || _deliveryJobId != job.Id)
        {
            return;
        }

        await bridge.AckAsync(tag, ct).ConfigureAwait(false);
        ClearDelivery();

        logger.LogInformation("Processing job completed; RabbitMQ delivery acked. jobId={JobId}", job.Id);
    }

    public async Task<JobFailureOutcome> FailAsync(ProcessingJob job, ProcessingError error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(error);

        var outcome = await _bookkeeping.FailAsync(job, error, _options, ct).ConfigureAwait(false);

        if (_deliveryTag is not { } tag || _deliveryJobId != job.Id)
        {
            return outcome;
        }

        try
        {
            if (outcome is { Applied: true, WillRetry: true })
            {
                // job.AttemptCount is the attempt that just failed (RetryBackoff.For's convention), which
                // is exactly the delay queue this provider declared for it.
                await bridge.PublishRetryAsync(job.AttemptCount, job.Id, job.DocumentId, ct).ConfigureAwait(false);
            }

            // Either the retry was just published to its delay queue, this is a definitive failure, or
            // another attempt already owns the job (Applied == false): the original delivery is spent
            // either way and must be acked so the broker does not hold or redeliver it.
            await bridge.AckAsync(tag, ct).ConfigureAwait(false);
        }
        finally
        {
            ClearDelivery();
        }

        return outcome;
    }

    public async Task ReleaseAsync(ProcessingJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        await _bookkeeping.ReleaseAsync(job, ct).ConfigureAwait(false);

        if (_deliveryTag is not { } tag || _deliveryJobId != job.Id)
        {
            return;
        }

        // Nacked with requeue=true so another consumer can pick it up right away, instead of waiting out
        // the broker's consumer_timeout - a clean shutdown is not the document's fault.
        await bridge.NackRequeueAsync(tag, ct).ConfigureAwait(false);
        ClearDelivery();

        logger.LogInformation("Processing job released back to RabbitMQ. jobId={JobId}", job.Id);
    }

    private void ClearDelivery()
    {
        _deliveryTag = null;
        _deliveryJobId = null;
    }
}
