using DocReader.Application.Documents;
using DocReader.Domain.Documents;
using DocReader.Domain.Idempotency;
using DocReader.Domain.Processing;
using DocReader.Domain.Retention;

namespace DocReader.Application.Abstractions;

/// <summary>
/// Persistence of documents and their asynchronous work.
/// </summary>
public interface IDocumentRepository
{
    /// <summary>
    /// Persists document, timeline, processing job and the optional idempotency record in a single
    /// transaction, so a document accepted with 202 always has a job (ADR 0001).
    /// </summary>
    Task AcceptAsync(
        Document document,
        ProcessingJob job,
        IdempotencyRecord? idempotencyRecord,
        CancellationToken ct);

    Task<Document?> FindByIdAsync(Guid id, bool includeEvents, CancellationToken ct);

    Task<Document?> FindByProtocolAsync(string protocol, bool includeEvents, CancellationToken ct);

    /// <summary>The most recently uploaded document whose external reference equals the given one, exactly.</summary>
    Task<Document?> FindLatestByExternalReferenceAsync(string externalReference, CancellationToken ct);

    Task<PagedResult<Document>> ListAsync(DocumentListFilter filter, CancellationToken ct);

    /// <summary>Most recent job of the document, whatever its status.</summary>
    Task<ProcessingJob?> FindLatestJobAsync(Guid documentId, CancellationToken ct);

    /// <summary>Light description of the most recent extraction, without text or fields.</summary>
    Task<ExtractionSummary?> FindLatestExtractionSummaryAsync(Guid documentId, CancellationToken ct);

    /// <summary>Most recent extraction with its fields, without the raw OCR payload.</summary>
    Task<ExtractionResultView?> FindLatestExtractionResultAsync(Guid documentId, CancellationToken ct);

    /// <summary>Most recent extraction with the text of each page, without the raw OCR payload.</summary>
    Task<ExtractionTextView?> FindLatestExtractionTextAsync(Guid documentId, CancellationToken ct);

    /// <summary>
    /// Most recent extraction with the stored OCR payload (blocks and coordinates), for replaying the
    /// extraction. Heavy: only the diagnostics ask for it.
    /// </summary>
    Task<ExtractionOcrView?> FindLatestExtractionOcrAsync(Guid documentId, CancellationToken ct);

    /// <summary>
    /// Queues a new processing attempt (RF-013). The check for an active job and the insert happen
    /// under a lock on the document row, so two concurrent requests cannot both succeed; a unique
    /// index on active jobs backs that up in the database.
    /// </summary>
    Task<ReprocessOutcome> QueueReprocessingAsync(
        Guid documentId,
        DateTimeOffset now,
        RetentionPolicy? retentionPolicy,
        CancellationToken ct);

    /// <summary>
    /// Documents past their purge date and in a final status, oldest first, skipping <paramref name="exclude"/>
    /// (the ones this run already failed on).
    /// </summary>
    Task<IReadOnlyList<PurgeCandidate>> FindPurgeableAsync(
        DateTimeOffset now,
        int limit,
        IReadOnlyCollection<Guid> exclude,
        CancellationToken ct);

    /// <summary>
    /// Marks a document PURGED if it is still eligible at that moment. Returns false when it no longer is
    /// (already purged, or reprocessed since it was listed), which is not an error.
    /// </summary>
    Task<bool> MarkPurgedAsync(Guid documentId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Removes the document, its timeline, jobs and results. Returns false when absent.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}
