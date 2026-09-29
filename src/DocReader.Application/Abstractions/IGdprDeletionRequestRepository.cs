using DocReader.Domain.GdprDeletion;

namespace DocReader.Application.Abstractions;

/// <summary>Document a GDPR deletion request's execution touched, for the file to be removed and the audit log.</summary>
public sealed record GdprDeletionExecutionCandidate(Guid RequestId, Guid DocumentId, Guid StorageRepositoryId, string StorageKey);

/// <summary>What one execution run did, for the audit log entry that follows it.</summary>
public sealed record GdprDeletionExecutionResult(Guid RequestId, Guid DocumentId, string? ApprovedBy);

/// <summary>
/// Persistence of GDPR/LGPD deletion requests. <see cref="AddAsync"/>, <see cref="ApproveAsync"/> and
/// <see cref="RejectAsync"/> also append the matching timeline event to the document, in the same transaction, so
/// the request and the document's history never disagree.
/// </summary>
public interface IGdprDeletionRequestRepository
{
    /// <summary>Persists the request and appends <c>GDPR_DELETION_REQUESTED</c> to the document's timeline.</summary>
    Task AddAsync(GdprDeletionRequest request, CancellationToken ct);

    Task<GdprDeletionRequest?> FindByIdAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<GdprDeletionRequest>> FindByDocumentIdAsync(Guid documentId, CancellationToken ct);

    /// <summary>
    /// Approves one PENDING request (locks it first) and appends <c>GDPR_DELETION_APPROVED</c> to the document's
    /// timeline. Returns null when the request does not exist or is no longer PENDING (already decided by another
    /// call, manual or automatic). Used both by the approval endpoint and by the auto-approve worker loop.
    /// </summary>
    Task<GdprDeletionRequest?> ApproveAsync(Guid requestId, string? approvedBy, DateTimeOffset now, CancellationToken ct);

    /// <summary>Rejects one PENDING request and appends <c>GDPR_DELETION_REJECTED</c> to the document's timeline.</summary>
    Task<GdprDeletionRequest?> RejectAsync(Guid requestId, DateTimeOffset now, CancellationToken ct);

    /// <summary>Ids of the requests still PENDING and requested at or before <paramref name="cutoff"/>, oldest first.</summary>
    Task<IReadOnlyList<Guid>> FindPendingRequestedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct);

    /// <summary>The oldest APPROVED request not executed yet, with what its execution needs to remove the file.</summary>
    Task<GdprDeletionExecutionCandidate?> FindNextApprovedAsync(CancellationToken ct);

    /// <summary>
    /// Deletes the document's OCR text and extracted fields, marks the document GDPR-deleted
    /// (<see cref="Domain.Documents.Document.MarkGdprDeleted"/>) and the request EXECUTED, all in one transaction.
    /// The caller must have already removed the file itself (<see cref="GdprDeletionExecutionCandidate"/>) before
    /// calling this, so a failure here never leaves the record claiming a deletion that did not happen. Returns
    /// null when the request is no longer APPROVED (already executed by another run).
    /// </summary>
    Task<GdprDeletionExecutionResult?> MarkExecutedAsync(Guid requestId, Guid documentId, DateTimeOffset now, CancellationToken ct);
}
