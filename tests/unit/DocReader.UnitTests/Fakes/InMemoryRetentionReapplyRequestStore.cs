using DocReader.Application.Abstractions;
using DocReader.Domain.Documents;
using DocReader.Domain.Retention;

namespace DocReader.UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="IRetentionReapplyRequestRepository"/>. The batch work runs against the documents held by
/// an <see cref="InMemoryDocumentStore"/>, exactly like the real repository runs it against the same database the
/// documents live in.
/// </summary>
public sealed class InMemoryRetentionReapplyRequestStore(InMemoryDocumentStore documents) : IRetentionReapplyRequestRepository
{
    public List<RetentionReapplyRequest> Items { get; } = [];

    public Task AddAsync(RetentionReapplyRequest request, CancellationToken ct)
    {
        Items.Add(request);

        return Task.CompletedTask;
    }

    public Task<RetentionReapplyRequest?> FindByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(request => request.Id == id));

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<RetentionReapplyRequest?> ClaimNextPendingAsync(DateTimeOffset now, CancellationToken ct)
    {
        var next = Items
            .Where(request => request.Status == RetentionReapplyStatus.Pending)
            .OrderBy(request => request.RequestedAt)
            .FirstOrDefault();

        next?.MarkRunning(now);

        return Task.FromResult(next);
    }

    public Task<int> ReapplyBatchAsync(RetentionPolicy policy, int batchSize, DateTimeOffset now, CancellationToken ct)
    {
        var candidates = documents.Documents
            .Where(document => document.RetentionPolicyId == policy.Id && document.RetentionDays != policy.RetentionDays)
            .OrderBy(document => document.UploadedAt)
            .ThenBy(document => document.Id)
            .Take(batchSize)
            .ToList();

        foreach (var document in candidates)
        {
            document.ReapplyRetention(policy);
            document.RecordProgress(
                DocumentEventTypes.RetentionPolicyReapplied,
                now,
                $"policyId={policy.Id} retentionDays={policy.RetentionDays}");
        }

        return Task.FromResult(candidates.Count);
    }
}
