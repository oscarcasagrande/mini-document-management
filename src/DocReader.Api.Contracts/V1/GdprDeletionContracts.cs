using DocReader.Domain.GdprDeletion;

namespace DocReader.Api.Contracts.V1;

/// <summary>
/// One GDPR/LGPD deletion request: the approvable alternative to the unconditional
/// <c>DELETE /api/v1/documents/{id}</c>. Once EXECUTED, the document's file, OCR text and extracted fields are
/// gone and it reads the same as a retention purge, but this record (and the document's timeline) says the
/// deletion came from a data-subject request instead.
/// </summary>
/// <param name="Id">Identity of the request.</param>
/// <param name="DocumentId">Document the request applies to.</param>
/// <param name="RequestedBy">Whoever asked for the deletion; null until OIDC/RBAC lands.</param>
/// <param name="RequestedAt">When the request was created, in UTC.</param>
/// <param name="Reason">Short operator-supplied reason; never document content.</param>
/// <param name="Status">PENDING, APPROVED, REJECTED or EXECUTED.</param>
/// <param name="ApprovedBy">Whoever approved it: an operator, or <c>system:auto-approve-24h</c> when the configured window elapsed; null while pending or when rejected.</param>
/// <param name="DecidedAt">When it was approved or rejected, in UTC; null while pending.</param>
/// <param name="ExecutedAt">When the content was actually removed, in UTC; null until then.</param>
public sealed record GdprDeletionRequestResponse(
    Guid Id,
    Guid DocumentId,
    string? RequestedBy,
    DateTimeOffset RequestedAt,
    string? Reason,
    GdprDeletionRequestStatus Status,
    string? ApprovedBy,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? ExecutedAt);
