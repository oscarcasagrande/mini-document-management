using DocReader.Application.Abstractions;
using DocReader.Application.Audit;
using DocReader.Application.Errors;
using DocReader.Domain;
using DocReader.Domain.GdprDeletion;
using Microsoft.Extensions.Logging;

namespace DocReader.Application.GdprDeletion;

/// <summary>
/// Request/approve/reject side of a GDPR/LGPD deletion: an explicit, auditable alternative to the unconditional
/// <c>DELETE /api/v1/documents/{id}</c>. Creating a request only validates and records it; a document's content is
/// removed later, once approved, by <see cref="GdprDeletionExecutionService"/>.
/// </summary>
public sealed class GdprDeletionRequestService(
    IGdprDeletionRequestRepository requests,
    IDocumentRepository documents,
    IProductServiceRepository productServices,
    AuditLogService auditLog,
    TimeProvider timeProvider,
    ILogger<GdprDeletionRequestService> logger)
{
    /// <exception cref="DocumentNotFoundException">No document with this id.</exception>
    /// <exception cref="ResourceConflictException">The document is linked to an active product or service.</exception>
    /// <exception cref="RequestValidationException">The document's retention period has not expired yet.</exception>
    public async Task<GdprDeletionRequest> RequestDeletionAsync(
        Guid documentId,
        string? requestedBy,
        string? reason,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct)
    {
        var document = await documents.FindByIdAsync(documentId, includeEvents: false, ct).ConfigureAwait(false)
            ?? throw new DocumentNotFoundException(documentId.ToString());

        if (document.ProductServiceId is { } productServiceId)
        {
            var productService = await productServices.FindByIdAsync(productServiceId, ct).ConfigureAwait(false);
            if (productService is { Active: true })
            {
                throw new ResourceConflictException(
                    "DOCUMENT_IN_USE_BY_PRODUCT_SERVICE",
                    $"Document in use by product {productService.Name}.");
            }
        }

        var now = timeProvider.GetUtcNow();
        if (document.ExpiresAt is { } expiresAt && expiresAt > now)
        {
            throw new RequestValidationException(
                "RETENTION_NOT_EXPIRED",
                $"Wait until {expiresAt:yyyy-MM-ddTHH:mm:ssZ} to request deletion.");
        }

        var request = GdprDeletionRequest.Create(Guid.CreateVersion7(now), documentId, requestedBy, reason, now);
        await requests.AddAsync(request, ct).ConfigureAwait(false);

        await auditLog.RecordAsync(
            requestedBy,
            "GDPR_DELETION_REQUESTED",
            "document",
            documentId.ToString(),
            ipAddress,
            userAgent,
            $"gdprDeletionRequestId={request.Id}",
            ct).ConfigureAwait(false);

        logger.LogInformation(
            "GDPR deletion requested. gdprDeletionRequestId={RequestId} documentId={DocumentId}",
            request.Id,
            documentId);

        return request;
    }

    /// <exception cref="ResourceNotFoundException">No request with this id.</exception>
    public async Task<GdprDeletionRequest> GetByIdAsync(Guid requestId, CancellationToken ct) =>
        await requests.FindByIdAsync(requestId, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("gdpr-deletion-request", requestId.ToString());

    /// <exception cref="DocumentNotFoundException">No document with this id.</exception>
    public async Task<IReadOnlyList<GdprDeletionRequest>> ListByDocumentAsync(Guid documentId, CancellationToken ct)
    {
        _ = await documents.FindByIdAsync(documentId, includeEvents: false, ct).ConfigureAwait(false)
            ?? throw new DocumentNotFoundException(documentId.ToString());

        return await requests.FindByDocumentIdAsync(documentId, ct).ConfigureAwait(false);
    }

    /// <exception cref="ResourceNotFoundException">No request with this id.</exception>
    /// <exception cref="ResourceConflictException">The request is not PENDING.</exception>
    public async Task<GdprDeletionRequest> ApproveAsync(
        Guid requestId,
        string? approvedBy,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct)
    {
        var existing = await GetByIdAsync(requestId, ct).ConfigureAwait(false);

        var approved = await requests.ApproveAsync(requestId, approvedBy, timeProvider.GetUtcNow(), ct).ConfigureAwait(false)
            ?? throw NotPending(existing, "approved");

        await auditLog.RecordAsync(
            approvedBy,
            "GDPR_DELETION_APPROVED",
            "gdpr-deletion-request",
            requestId.ToString(),
            ipAddress,
            userAgent,
            $"documentId={approved.DocumentId}",
            ct).ConfigureAwait(false);

        logger.LogInformation(
            "GDPR deletion request approved. gdprDeletionRequestId={RequestId} documentId={DocumentId}",
            requestId,
            approved.DocumentId);

        return approved;
    }

    /// <exception cref="ResourceNotFoundException">No request with this id.</exception>
    /// <exception cref="ResourceConflictException">The request is not PENDING.</exception>
    public async Task<GdprDeletionRequest> RejectAsync(
        Guid requestId,
        string? rejectedBy,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct)
    {
        var existing = await GetByIdAsync(requestId, ct).ConfigureAwait(false);

        var rejected = await requests.RejectAsync(requestId, timeProvider.GetUtcNow(), ct).ConfigureAwait(false)
            ?? throw NotPending(existing, "rejected");

        await auditLog.RecordAsync(
            rejectedBy,
            "GDPR_DELETION_REJECTED",
            "gdpr-deletion-request",
            requestId.ToString(),
            ipAddress,
            userAgent,
            $"documentId={rejected.DocumentId}",
            ct).ConfigureAwait(false);

        logger.LogInformation(
            "GDPR deletion request rejected. gdprDeletionRequestId={RequestId} documentId={DocumentId}",
            requestId,
            rejected.DocumentId);

        return rejected;
    }

    private static ResourceConflictException NotPending(GdprDeletionRequest request, string action) =>
        new(
            "GDPR_DELETION_REQUEST_NOT_PENDING",
            $"The request is {EnumNaming.ToUpperSnakeCase(request.Status)} and can no longer be {action}.");
}
