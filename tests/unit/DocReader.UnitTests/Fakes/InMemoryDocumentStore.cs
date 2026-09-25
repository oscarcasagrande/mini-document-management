using DocReader.Application.Abstractions;
using DocReader.Application.Documents;
using DocReader.Domain.Documents;
using DocReader.Domain.Idempotency;
using DocReader.Domain.Processing;

namespace DocReader.UnitTests.Fakes;

/// <summary>
/// Minimal in memory stand in for the repository and the idempotency store, so the upload rules can
/// be tested without a database.
/// </summary>
public sealed class InMemoryDocumentStore : IDocumentRepository, IIdempotencyStore
{
    private readonly Dictionary<Guid, Document> _documents = [];
    private readonly Dictionary<string, IdempotencyRecord> _keys = new(StringComparer.Ordinal);

    public List<ProcessingJob> Jobs { get; } = [];

    /// <summary>Latest extraction per document, as the read side of the repository would return it.</summary>
    public Dictionary<Guid, (ExtractionResultView Result, ExtractionTextView Text)> Extractions { get; } = [];

    public IReadOnlyCollection<Document> Documents => _documents.Values;

    public IReadOnlyDictionary<string, IdempotencyRecord> Keys => _keys;

    /// <summary>When set, <see cref="AcceptAsync"/> throws, to exercise the rollback path.</summary>
    public Exception? AcceptFailure { get; set; }

    public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;

    public Task AcceptAsync(
        Document document,
        ProcessingJob job,
        IdempotencyRecord? idempotencyRecord,
        CancellationToken ct)
    {
        if (AcceptFailure is not null)
        {
            return Task.FromException(AcceptFailure);
        }

        _documents[document.Id] = document;
        Jobs.Add(job);

        if (idempotencyRecord is not null)
        {
            _keys[idempotencyRecord.Key] = idempotencyRecord;
        }

        return Task.CompletedTask;
    }

    public Task<Document?> FindByIdAsync(Guid id, bool includeEvents, CancellationToken ct) =>
        Task.FromResult(_documents.TryGetValue(id, out var document) ? document : null);

    public Task<Document?> FindByProtocolAsync(string protocol, bool includeEvents, CancellationToken ct) =>
        Task.FromResult(_documents.Values.FirstOrDefault(document => document.Protocol == protocol));

    public Task<PagedResult<Document>> ListAsync(DocumentListFilter filter, CancellationToken ct)
    {
        var ordered = _documents.Values
            .OrderByDescending(document => document.UploadedAt)
            .ToArray();

        var page = ordered
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToArray();

        return Task.FromResult(new PagedResult<Document>(page, filter.Page, filter.PageSize, ordered.Length));
    }

    public Task<ProcessingJob?> FindLatestJobAsync(Guid documentId, CancellationToken ct) =>
        Task.FromResult(Jobs.LastOrDefault(job => job.DocumentId == documentId));

    public Task<ExtractionSummary?> FindLatestExtractionSummaryAsync(Guid documentId, CancellationToken ct) =>
        Task.FromResult(Extractions.TryGetValue(documentId, out var entry) ? entry.Result.Summary : null);

    public Task<ExtractionResultView?> FindLatestExtractionResultAsync(Guid documentId, CancellationToken ct) =>
        Task.FromResult(Extractions.TryGetValue(documentId, out var entry) ? entry.Result : null);

    public Task<ExtractionTextView?> FindLatestExtractionTextAsync(Guid documentId, CancellationToken ct) =>
        Task.FromResult(Extractions.TryGetValue(documentId, out var entry) ? entry.Text : null);

    /// <summary>Payload OCR gravado por documento; sem entrada, a extração só tem o texto das páginas.</summary>
    public Dictionary<Guid, string> RawOcr { get; } = [];

    public Task<ExtractionOcrView?> FindLatestExtractionOcrAsync(Guid documentId, CancellationToken ct) =>
        Task.FromResult<ExtractionOcrView?>(Extractions.TryGetValue(documentId, out var entry)
            ? new ExtractionOcrView(entry.Text.Summary, RawOcr.GetValueOrDefault(documentId, "{}"), entry.Text.PageTextsJson)
            : null);

    public Task<ReprocessOutcome> QueueReprocessingAsync(Guid documentId, DateTimeOffset now, CancellationToken ct)
    {
        if (!_documents.TryGetValue(documentId, out var document))
        {
            return Task.FromResult(ReprocessOutcome.NotFound);
        }

        var hasActiveJob = Jobs.Any(job =>
            job.DocumentId == documentId &&
            job.Status is ProcessingJobStatus.Pending or ProcessingJobStatus.Running);

        if (hasActiveJob || document.Status is not (DocumentStatus.Stored or DocumentStatus.Failed or DocumentStatus.Completed))
        {
            return Task.FromResult(ReprocessOutcome.Conflict);
        }

        document.MarkQueued(now, "REPROCESS_REQUESTED");
        Jobs.Add(ProcessingJob.CreateForDocument(documentId, now));

        return Task.FromResult(ReprocessOutcome.Queued);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var removed = _documents.Remove(id);
        Jobs.RemoveAll(job => job.DocumentId == id);
        foreach (var key in _keys.Where(entry => entry.Value.DocumentId == id).Select(entry => entry.Key).ToArray())
        {
            _keys.Remove(key);
        }

        return Task.FromResult(removed);
    }

    public Task<IdempotencyRecord?> FindLiveAsync(string key, CancellationToken ct)
    {
        if (!_keys.TryGetValue(key, out var record))
        {
            return Task.FromResult<IdempotencyRecord?>(null);
        }

        if (record.IsExpired(Now))
        {
            _keys.Remove(key);
            return Task.FromResult<IdempotencyRecord?>(null);
        }

        return Task.FromResult<IdempotencyRecord?>(record);
    }
}
