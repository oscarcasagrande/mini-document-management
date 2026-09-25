using DocReader.Domain.Processing;

namespace DocReader.Application.Abstractions;

/// <summary>
/// Queue contract of PRD section 11. The PoC implementation is the <c>processing_jobs</c> table
/// consumed with <c>FOR UPDATE SKIP LOCKED</c>; see ADR 0001.
/// </summary>
public interface IProcessingQueue
{
    Task EnqueueAsync(Guid documentId, CancellationToken ct);

    Task<ProcessingJob?> AcquireNextAsync(CancellationToken ct);

    /// <summary>
    /// Renews the lock of a running job and records its progress. Returns false when the worker no
    /// longer owns the job, because it was recovered as stuck and handed to another attempt; the
    /// caller must stop without writing anything (ADR 0002).
    /// </summary>
    Task<bool> HeartbeatAsync(ProcessingJob job, int pagesCompleted, int pageCount, CancellationToken ct);

    /// <summary>Completes the job. Ignored when the caller no longer owns it.</summary>
    Task CompleteAsync(ProcessingJob job, CancellationToken ct);

    /// <summary>
    /// Schedules a retry or fails the job for good, and reflects it on the document. Nothing is
    /// written when the caller no longer owns the job.
    /// </summary>
    Task<JobFailureOutcome> FailAsync(ProcessingJob job, ProcessingError error, CancellationToken ct);

    /// <summary>
    /// Gives a job back without counting the attempt, for a worker that is shutting down cleanly.
    /// Without it a restart would leave the job locked until the heartbeat timeout expires.
    /// </summary>
    Task ReleaseAsync(ProcessingJob job, CancellationToken ct);
}

/// <summary>What <see cref="IProcessingQueue.FailAsync"/> decided for the job.</summary>
/// <param name="Applied">False when the caller no longer owned the job and nothing was written.</param>
/// <param name="WillRetry">True when the job went back to the queue.</param>
/// <param name="AvailableAt">Earliest moment of the next attempt, when retrying.</param>
public sealed record JobFailureOutcome(bool Applied, bool WillRetry, DateTimeOffset? AvailableAt);
