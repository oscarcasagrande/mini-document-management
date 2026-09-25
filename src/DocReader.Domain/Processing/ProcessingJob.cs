using DocReader.Domain.Documents;

namespace DocReader.Domain.Processing;

/// <summary>
/// Unit of asynchronous work. Persisted in the same transaction as the document, so a document
/// accepted with 202 always has a job (ADR 0001).
/// </summary>
public sealed class ProcessingJob
{
    private ProcessingJob()
    {
    }

    public Guid Id { get; private init; }

    public Guid DocumentId { get; private init; }

    public ProcessingJobStatus Status { get; private set; }

    /// <summary>Document stage this job is currently driving.</summary>
    public DocumentStatus Stage { get; private set; }

    public int AttemptCount { get; private set; }

    /// <summary>Earliest moment the job may be acquired; moved forward by the retry backoff.</summary>
    public DateTimeOffset AvailableAt { get; private set; }

    /// <summary>
    /// Last sign of life of the worker that holds the job. It is set when the job is acquired and
    /// renewed by every heartbeat, so the recovery of a stuck job measures the time since the last
    /// page and never the total duration of the document (ADR 0002).
    /// </summary>
    public DateTimeOffset? LockedAt { get; private set; }

    public string? LockedBy { get; private set; }

    /// <summary>Pages already read in the current attempt. Reset to zero when the job is acquired.</summary>
    public int PagesCompleted { get; private set; }

    /// <summary>Pages the current attempt has to read, known once the worker starts.</summary>
    public int? PageCount { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public static ProcessingJob CreateForDocument(Guid documentId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            Status = ProcessingJobStatus.Pending,
            Stage = DocumentStatus.Queued,
            AttemptCount = 0,
            AvailableAt = now,
            CreatedAt = now
        };
}
