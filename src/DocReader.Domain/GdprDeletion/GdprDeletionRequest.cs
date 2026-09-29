namespace DocReader.Domain.GdprDeletion;

/// <summary>
/// One request to erase a document under a data-subject (LGPD/GDPR) deletion request: an explicit, approvable
/// alternative to the unconditional <c>DELETE /api/v1/documents/{id}</c>. Approval is manual (an operator) or
/// automatic, after a configured window, when nobody decides in time. Once approved, the worker executes it:
/// the file, the OCR text and the extracted fields are removed and the document becomes a tombstone, exactly
/// like a retention purge, but recorded as its own kind of event on the timeline.
/// </summary>
public sealed class GdprDeletionRequest
{
    private GdprDeletionRequest()
    {
    }

    public Guid Id { get; private init; }

    public Guid DocumentId { get; private init; }

    /// <summary>Whoever asked for the deletion. Null today: the PoC has no authentication yet.</summary>
    public string? RequestedBy { get; private init; }

    public DateTimeOffset RequestedAt { get; private init; }

    /// <summary>Short operator-supplied reason. Never document content: just why the deletion was asked for.</summary>
    public string? Reason { get; private init; }

    public GdprDeletionRequestStatus Status { get; private set; }

    /// <summary>
    /// Whoever approved the request: an operator's identity, or <c>system:auto-approve-24h</c> when the
    /// configured window elapsed with nobody deciding. Null while pending or when rejected.
    /// </summary>
    public string? ApprovedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public DateTimeOffset? ExecutedAt { get; private set; }

    public static GdprDeletionRequest Create(Guid id, Guid documentId, string? requestedBy, string? reason, DateTimeOffset now) => new()
    {
        Id = id,
        DocumentId = documentId,
        RequestedBy = requestedBy,
        Reason = reason,
        RequestedAt = now,
        Status = GdprDeletionRequestStatus.Pending
    };

    /// <exception cref="InvalidOperationException">The request is not PENDING.</exception>
    public void Approve(string? approvedBy, DateTimeOffset now)
    {
        RequireStatus(GdprDeletionRequestStatus.Pending, nameof(Approve));

        Status = GdprDeletionRequestStatus.Approved;
        ApprovedBy = approvedBy;
        DecidedAt = now;
    }

    /// <exception cref="InvalidOperationException">The request is not PENDING.</exception>
    public void Reject(DateTimeOffset now)
    {
        RequireStatus(GdprDeletionRequestStatus.Pending, nameof(Reject));

        Status = GdprDeletionRequestStatus.Rejected;
        DecidedAt = now;
    }

    /// <exception cref="InvalidOperationException">The request is not APPROVED.</exception>
    public void MarkExecuted(DateTimeOffset now)
    {
        RequireStatus(GdprDeletionRequestStatus.Approved, nameof(MarkExecuted));

        Status = GdprDeletionRequestStatus.Executed;
        ExecutedAt = now;
    }

    private void RequireStatus(GdprDeletionRequestStatus expected, string action)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Cannot {action} a GDPR deletion request in status {Status}.");
        }
    }
}
