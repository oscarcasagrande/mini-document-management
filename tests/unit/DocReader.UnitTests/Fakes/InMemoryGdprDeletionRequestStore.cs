using DocReader.Application.Abstractions;
using DocReader.Domain.Documents;
using DocReader.Domain.GdprDeletion;

namespace DocReader.UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="IGdprDeletionRequestRepository"/>. Mirrors what the real repository does against the
/// documents held by an <see cref="InMemoryDocumentStore"/>: appends the matching timeline event on every
/// transition, and treats a request no longer in the expected status as a lost race (returns null) rather than
/// throwing, exactly like the real lock-and-recheck transactions.
/// </summary>
public sealed class InMemoryGdprDeletionRequestStore(InMemoryDocumentStore documents) : IGdprDeletionRequestRepository
{
    public List<GdprDeletionRequest> Items { get; } = [];

    public Task AddAsync(GdprDeletionRequest request, CancellationToken ct)
    {
        Items.Add(request);

        if (documents.Documents.FirstOrDefault(document => document.Id == request.DocumentId) is { } document)
        {
            document.RecordProgress(DocumentEventTypes.GdprDeletionRequested, request.RequestedAt, $"gdprDeletionRequestId={request.Id}");
        }

        return Task.CompletedTask;
    }

    public Task<GdprDeletionRequest?> FindByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(request => request.Id == id));

    public Task<IReadOnlyList<GdprDeletionRequest>> FindByDocumentIdAsync(Guid documentId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GdprDeletionRequest>>([
            .. Items.Where(request => request.DocumentId == documentId).OrderByDescending(request => request.RequestedAt)
        ]);

    public Task<GdprDeletionRequest?> ApproveAsync(Guid requestId, string? approvedBy, DateTimeOffset now, CancellationToken ct) =>
        Decide(requestId, request => request.Approve(approvedBy, now), DocumentEventTypes.GdprDeletionApproved, now,
            request => $"gdprDeletionRequestId={request.Id} approvedBy={approvedBy ?? "null"}");

    public Task<GdprDeletionRequest?> RejectAsync(Guid requestId, DateTimeOffset now, CancellationToken ct) =>
        Decide(requestId, request => request.Reject(now), DocumentEventTypes.GdprDeletionRejected, now,
            request => $"gdprDeletionRequestId={request.Id}");

    private Task<GdprDeletionRequest?> Decide(
        Guid requestId,
        Action<GdprDeletionRequest> decide,
        string documentEventType,
        DateTimeOffset now,
        Func<GdprDeletionRequest, string> documentEventDetails)
    {
        var request = Items.FirstOrDefault(item => item.Id == requestId);
        if (request is null || request.Status != GdprDeletionRequestStatus.Pending)
        {
            return Task.FromResult<GdprDeletionRequest?>(null);
        }

        decide(request);

        if (documents.Documents.FirstOrDefault(document => document.Id == request.DocumentId) is { } document)
        {
            document.RecordProgress(documentEventType, now, documentEventDetails(request));
        }

        return Task.FromResult<GdprDeletionRequest?>(request);
    }

    public Task<IReadOnlyList<Guid>> FindPendingRequestedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>([
            .. Items
                .Where(request => request.Status == GdprDeletionRequestStatus.Pending && request.RequestedAt <= cutoff)
                .OrderBy(request => request.RequestedAt)
                .Select(request => request.Id)
        ]);

    public Task<GdprDeletionExecutionCandidate?> FindNextApprovedAsync(CancellationToken ct)
    {
        var next = Items
            .Where(request => request.Status == GdprDeletionRequestStatus.Approved)
            .OrderBy(request => request.RequestedAt)
            .FirstOrDefault();

        if (next is null)
        {
            return Task.FromResult<GdprDeletionExecutionCandidate?>(null);
        }

        var document = documents.Documents.First(candidate => candidate.Id == next.DocumentId);

        return Task.FromResult<GdprDeletionExecutionCandidate?>(
            new GdprDeletionExecutionCandidate(next.Id, document.Id, document.StorageRepositoryId, document.StorageKey));
    }

    public Task<GdprDeletionExecutionResult?> MarkExecutedAsync(Guid requestId, Guid documentId, DateTimeOffset now, CancellationToken ct)
    {
        var request = Items.FirstOrDefault(item => item.Id == requestId);
        if (request is null || request.Status != GdprDeletionRequestStatus.Approved)
        {
            return Task.FromResult<GdprDeletionExecutionResult?>(null);
        }

        var document = documents.Documents.FirstOrDefault(candidate => candidate.Id == documentId);
        if (document is not null && document.Status != DocumentStatus.Purged)
        {
            var deleted = new List<string> { PurgedContent.File };
            if (documents.Extractions.Remove(documentId, out var extraction))
            {
                documents.RawOcr.Remove(documentId);
                deleted.Add(PurgedContent.OcrText);
                if (extraction.Result.Fields.Count > 0)
                {
                    deleted.Add(PurgedContent.ExtractedFields);
                }
            }

            document.MarkGdprDeleted(now, requestId, deleted);
        }

        request.MarkExecuted(now);

        return Task.FromResult<GdprDeletionExecutionResult?>(
            new GdprDeletionExecutionResult(request.Id, documentId, request.ApprovedBy));
    }
}
