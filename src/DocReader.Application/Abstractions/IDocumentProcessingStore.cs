using DocReader.Domain.Documents;
using DocReader.Domain.Extractions;
using DocReader.Domain.Processing;
using DocReader.Domain.Retention;

namespace DocReader.Application.Abstractions;

/// <summary>
/// Writes of the worker: stage transitions, progress events and the final result. Kept apart from
/// <see cref="IDocumentRepository"/> so the API side has no way to complete a document by accident.
/// </summary>
public interface IDocumentProcessingStore
{
    Task<Document?> FindDocumentAsync(Guid documentId, CancellationToken ct);

    /// <summary>Moves the document to <paramref name="stage"/> and records the event.</summary>
    Task AdvanceStageAsync(
        Guid documentId,
        DocumentStatus stage,
        string eventType,
        string? details,
        CancellationToken ct);

    /// <summary>Records progress inside the current stage, such as a page that was read.</summary>
    Task RecordProgressAsync(Guid documentId, string eventType, string? details, CancellationToken ct);

    /// <summary>
    /// Persists the extraction, marks the document COMPLETED and completes the job, all in one
    /// transaction. Returns false, writing nothing, when the job is no longer owned by
    /// <paramref name="job"/>: a result from a worker that lost its lock must be discarded.
    /// </summary>
    Task<bool> CompleteAsync(
        ProcessingJob job,
        DocumentExtraction extraction,
        string detectedDocumentType,
        decimal? classificationConfidence,
        string? classificationDetails,
        RetentionPolicy? retentionPolicy,
        CancellationToken ct);
}
