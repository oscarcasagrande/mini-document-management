using DocReader.Application.Abstractions;
using DocReader.Domain.Documents;
using DocReader.Domain.StorageMigrations;

namespace DocReader.UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="IStorageMigrationJobRepository"/>. The per-document work runs against the documents held by an
/// <see cref="InMemoryDocumentStore"/>, exactly like the real repository runs it against the same database the documents
/// live in, with the same eligibility rule.
/// </summary>
public sealed class InMemoryStorageMigrationJobStore(InMemoryDocumentStore documents) : IStorageMigrationJobRepository
{
    public List<StorageMigrationJob> Items { get; } = [];

    /// <summary>Called before each status check, so a test can cancel a job between batches.</summary>
    public Action<StorageMigrationJob>? BeforeStatusCheck { get; set; }

    /// <summary>How many batches were selected.</summary>
    public int BatchesSelected { get; private set; }

    public Task AddAsync(StorageMigrationJob job, CancellationToken ct)
    {
        Items.Add(job);

        return Task.CompletedTask;
    }

    public Task<StorageMigrationJob?> FindByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(job => job.Id == id));

    public Task<StorageMigrationJob?> ClaimNextPendingAsync(DateTimeOffset now, CancellationToken ct)
    {
        var next = Items
            .Where(job => job.Status == StorageMigrationStatus.Pending)
            .OrderBy(job => job.RequestedAt)
            .FirstOrDefault();

        next?.MarkRunning(now);

        return Task.FromResult(next);
    }

    public Task<StorageMigrationStatus?> GetStatusAsync(Guid id, CancellationToken ct)
    {
        var job = Items.FirstOrDefault(candidate => candidate.Id == id);
        if (job is not null)
        {
            BeforeStatusCheck?.Invoke(job);
        }

        return Task.FromResult(job?.Status);
    }

    public Task<StorageMigrationJob?> UpdateLockedAsync(Guid id, Action<StorageMigrationJob> change, CancellationToken ct)
    {
        var job = Items.FirstOrDefault(candidate => candidate.Id == id);
        if (job is not null)
        {
            change(job);
        }

        return Task.FromResult(job);
    }

    public Task<IReadOnlyList<Guid>> FindBatchAsync(
        StorageMigrationJob job,
        int batchSize,
        IReadOnlyCollection<Guid> excludedIds,
        CancellationToken ct)
    {
        BatchesSelected++;

        IReadOnlyList<Guid> batch = documents.Documents
            .Where(document => IsEligible(document, job) && !excludedIds.Contains(document.Id))
            .OrderBy(document => document.UploadedAt)
            .ThenBy(document => document.Id)
            .Select(document => document.Id)
            .Take(batchSize)
            .ToList();

        return Task.FromResult(batch);
    }

    public async Task<bool> MigrateDocumentAsync(
        Guid documentId,
        StorageMigrationJob job,
        Func<Document, CancellationToken, Task<string>> copyAsync,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var document = documents.Documents.FirstOrDefault(candidate => candidate.Id == documentId);
        if (document is null || !IsEligible(document, job))
        {
            return false;
        }

        var key = await copyAsync(document, ct);
        document.MigrateStorageRepository(job.TargetRepositoryId, key, now, job.Id);

        return true;
    }

    private static bool IsEligible(Document document, StorageMigrationJob job)
    {
        var marker = StorageMigrationEventDetails.JobMarker(job.Id);

        return document.StorageRepositoryId == job.SourceRepositoryId
            && document.Status != DocumentStatus.Purged
            && !document.Events.Any(entry =>
                entry.EventType == DocumentEventTypes.StorageMigrated && entry.Details?.Contains(marker, StringComparison.Ordinal) == true)
            && (job.FilterDocumentType is null || document.DetectedDocumentType == job.FilterDocumentType)
            && (job.FilterProductServiceId is null || document.ProductServiceId == job.FilterProductServiceId)
            && (job.FilterUploadedFrom is null || document.UploadedAt >= job.FilterUploadedFrom)
            && (job.FilterUploadedTo is null || document.UploadedAt <= job.FilterUploadedTo);
    }
}
