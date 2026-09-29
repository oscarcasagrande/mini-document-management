using DocReader.Application.Abstractions;
using DocReader.Domain.Documents;
using DocReader.Domain.GdprDeletion;
using Microsoft.EntityFrameworkCore;

namespace DocReader.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of GDPR/LGPD deletion requests. Every transition that changes both a request and its
/// document (add, approve, reject, execute) locks the request row first (<c>FOR UPDATE</c>) and rechecks its
/// status, the same idiom <see cref="RetentionReapplyRequestRepository"/> and <see cref="DocumentRepository"/> use
/// for their own claim-and-mutate operations, so two callers racing on the same request never both win.
/// </summary>
public sealed class GdprDeletionRequestRepository(DocReaderDbContext dbContext) : IGdprDeletionRequestRepository
{
    public async Task AddAsync(GdprDeletionRequest request, CancellationToken ct)
    {
        var document = await dbContext.Documents
            .FirstAsync(candidate => candidate.Id == request.DocumentId, ct)
            .ConfigureAwait(false);

        document.RecordProgress(DocumentEventTypes.GdprDeletionRequested, request.RequestedAt, $"gdprDeletionRequestId={request.Id}");

        dbContext.GdprDeletionRequests.Add(request);

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<GdprDeletionRequest?> FindByIdAsync(Guid id, CancellationToken ct) =>
        dbContext.GdprDeletionRequests.FirstOrDefaultAsync(request => request.Id == id, ct);

    public async Task<IReadOnlyList<GdprDeletionRequest>> FindByDocumentIdAsync(Guid documentId, CancellationToken ct) =>
        await dbContext.GdprDeletionRequests
            .AsNoTracking()
            .Where(request => request.DocumentId == documentId)
            .OrderByDescending(request => request.RequestedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public Task<GdprDeletionRequest?> ApproveAsync(Guid requestId, string? approvedBy, DateTimeOffset now, CancellationToken ct) =>
        DecideAsync(requestId, now, ct, (request, at) => request.Approve(approvedBy, at), DocumentEventTypes.GdprDeletionApproved,
            request => $"gdprDeletionRequestId={request.Id} approvedBy={approvedBy ?? "null"}");

    public Task<GdprDeletionRequest?> RejectAsync(Guid requestId, DateTimeOffset now, CancellationToken ct) =>
        DecideAsync(requestId, now, ct, (request, at) => request.Reject(at), DocumentEventTypes.GdprDeletionRejected,
            request => $"gdprDeletionRequestId={request.Id}");

    private async Task<GdprDeletionRequest?> DecideAsync(
        Guid requestId,
        DateTimeOffset now,
        CancellationToken ct,
        Action<GdprDeletionRequest, DateTimeOffset> decide,
        string documentEventType,
        Func<GdprDeletionRequest, string> documentEventDetails)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async cancellationToken =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            var locked = await dbContext.Database
                .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM gdpr_deletion_requests WHERE id = {requestId} FOR UPDATE")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (locked.Count == 0)
            {
                return null;
            }

            var request = await dbContext.GdprDeletionRequests
                .FirstAsync(candidate => candidate.Id == requestId, cancellationToken)
                .ConfigureAwait(false);

            if (request.Status != GdprDeletionRequestStatus.Pending)
            {
                return null;
            }

            decide(request, now);

            var document = await dbContext.Documents
                .FirstAsync(candidate => candidate.Id == request.DocumentId, cancellationToken)
                .ConfigureAwait(false);
            document.RecordProgress(documentEventType, now, documentEventDetails(request));

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return request;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> FindPendingRequestedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct) =>
        await dbContext.GdprDeletionRequests
            .AsNoTracking()
            .Where(request => request.Status == GdprDeletionRequestStatus.Pending && request.RequestedAt <= cutoff)
            .OrderBy(request => request.RequestedAt)
            .Select(request => request.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<GdprDeletionExecutionCandidate?> FindNextApprovedAsync(CancellationToken ct) =>
        await dbContext.GdprDeletionRequests
            .AsNoTracking()
            .Where(request => request.Status == GdprDeletionRequestStatus.Approved)
            .OrderBy(request => request.RequestedAt)
            .Join(
                dbContext.Documents.AsNoTracking(),
                request => request.DocumentId,
                document => document.Id,
                (request, document) => new GdprDeletionExecutionCandidate(
                    request.Id, document.Id, document.StorageRepositoryId, document.StorageKey))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task<GdprDeletionExecutionResult?> MarkExecutedAsync(
        Guid requestId,
        Guid documentId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async cancellationToken =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            var lockedRequest = await dbContext.Database
                .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM gdpr_deletion_requests WHERE id = {requestId} FOR UPDATE")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (lockedRequest.Count == 0)
            {
                return null;
            }

            var request = await dbContext.GdprDeletionRequests
                .FirstAsync(candidate => candidate.Id == requestId, cancellationToken)
                .ConfigureAwait(false);

            if (request.Status != GdprDeletionRequestStatus.Approved)
            {
                return null;
            }

            var lockedDocument = await dbContext.Database
                .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM documents WHERE id = {documentId} FOR UPDATE")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (lockedDocument.Count == 0)
            {
                return null;
            }

            var document = await dbContext.Documents
                .FirstAsync(candidate => candidate.Id == documentId, cancellationToken)
                .ConfigureAwait(false);

            // Already a tombstone by another path (e.g. retention purge got there first): nothing left to remove,
            // but the request still completes since the goal (content gone) is already achieved.
            if (document.Status != DocumentStatus.Purged)
            {
                var deleted = await ExtractionCleanup.DeleteExtractionsAsync(dbContext, documentId, cancellationToken).ConfigureAwait(false);
                document.MarkGdprDeleted(now, requestId, deleted);
                await WebhookOutbox.EnqueueAsync(dbContext, document, now, cancellationToken).ConfigureAwait(false);
            }

            request.MarkExecuted(now);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new GdprDeletionExecutionResult(request.Id, document.Id, request.ApprovedBy);
        }, ct).ConfigureAwait(false);
    }
}
