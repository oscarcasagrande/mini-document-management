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
