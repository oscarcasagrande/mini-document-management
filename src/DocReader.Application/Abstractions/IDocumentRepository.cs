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

    /// <summary>Removes the document, its timeline, jobs and results. Returns false when absent.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}
