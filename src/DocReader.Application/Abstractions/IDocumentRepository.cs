using DocReader.Application.Documents;
using DocReader.Domain.Documents;
using DocReader.Domain.Idempotency;
using DocReader.Domain.Processing;

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
    Task<ReprocessOutcome> QueueReprocessingAsync(Guid documentId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Removes the document, its timeline, jobs and results. Returns false when absent.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}
