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

    Task CompleteAsync(Guid jobId, CancellationToken ct);

    Task FailAsync(Guid jobId, ProcessingError error, CancellationToken ct);
}
