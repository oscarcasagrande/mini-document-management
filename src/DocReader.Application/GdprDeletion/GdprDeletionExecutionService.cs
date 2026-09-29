using DocReader.Application.Abstractions;
using DocReader.Application.Audit;
using DocReader.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocReader.Application.GdprDeletion;

/// <summary>
/// The worker-driven half of a GDPR/LGPD deletion: auto-approving requests nobody decided on in time, and
/// executing approved ones. Callable directly, without a running worker loop, which is what makes it testable
/// (the same shape as <see cref="Retention.RetentionReapplyService.ProcessAsync"/>).
/// </summary>
public sealed class GdprDeletionExecutionService(
    IGdprDeletionRequestRepository requests,
    IFileStorage storage,
    AuditLogService auditLog,
    IOptions<GdprDeletionOptions> options,
    TimeProvider timeProvider,
    ILogger<GdprDeletionExecutionService> logger)
{
    /// <summary>
    /// Approves every request still PENDING past <see cref="GdprDeletionOptions.AutoApproveAfter"/>, one at a time
    /// so each keeps the same lock-and-recheck safety as a manual approval. Returns how many were approved.
    /// </summary>
    public async Task<int> AutoApprovePastDueAsync(CancellationToken ct)
    {
        var cutoff = timeProvider.GetUtcNow() - options.Value.AutoApproveAfter;
        var due = await requests.FindPendingRequestedBeforeAsync(cutoff, ct).ConfigureAwait(false);

        var approved = 0;
        foreach (var requestId in due)
        {
            var result = await requests
                .ApproveAsync(requestId, GdprDeletionOptions.AutoApprovedBy, timeProvider.GetUtcNow(), ct)
                .ConfigureAwait(false);

            if (result is null)
            {
                continue;
            }

            approved++;

            await auditLog.RecordAsync(
                GdprDeletionOptions.AutoApprovedBy,
                "GDPR_DELETION_APPROVED",
                "gdpr-deletion-request",
                requestId.ToString(),
                null,
                null,
                $"documentId={result.DocumentId} autoApproveAfter={options.Value.AutoApproveAfter}",
                ct).ConfigureAwait(false);

            logger.LogInformation(
                "GDPR deletion request auto-approved after the configured window. gdprDeletionRequestId={RequestId} documentId={DocumentId}",
                requestId,
                result.DocumentId);
        }

        return approved;
    }

    /// <summary>
    /// Executes the oldest APPROVED request: removes the file, then the OCR text and the extracted fields, and
    /// marks the document and the request done. The file is removed before anything is written to the database,
    /// so a failure here never leaves a record claiming a deletion that did not happen; the request stays APPROVED
    /// and the next call tries again. Returns whether one was executed.
    /// </summary>
    public async Task<bool> ExecuteNextApprovedAsync(CancellationToken ct)
    {
        var candidate = await requests.FindNextApprovedAsync(ct).ConfigureAwait(false);
        if (candidate is null)
        {
            return false;
        }

        try
        {
            await storage.DeleteAsync(candidate.StorageRepositoryId, candidate.StorageKey, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "GDPR deletion could not remove the file; it will be retried. gdprDeletionRequestId={RequestId} documentId={DocumentId} errorType={ErrorType}",
                candidate.RequestId,
                candidate.DocumentId,
                exception.GetType().Name);

            return false;
        }

        var now = timeProvider.GetUtcNow();
        var executed = await requests
            .MarkExecutedAsync(candidate.RequestId, candidate.DocumentId, now, ct)
            .ConfigureAwait(false);

        if (executed is null)
        {
            // Lost the race to another run (or the request was rejected in the meantime): nothing more to do.
            return false;
        }

        await auditLog.RecordAsync(
            null,
            "GDPR_DELETION_EXECUTED",
            "document",
            executed.DocumentId.ToString(),
            null,
            null,
            $"gdprDeletionRequestId={executed.RequestId} approvedBy={executed.ApprovedBy ?? "unknown"}",
            ct).ConfigureAwait(false);

        logger.LogInformation(
            "GDPR deletion executed. gdprDeletionRequestId={RequestId} documentId={DocumentId}",
            executed.RequestId,
            executed.DocumentId);

        return true;
    }
}
