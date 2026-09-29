using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using DocReader.Domain.Retention;
using Microsoft.Extensions.Logging;

namespace DocReader.Application.Retention;

/// <summary>
/// Recomputes <c>expiresAt</c> on every existing document that carries a retention policy, after the policy's
/// <see cref="RetentionPolicy.RetentionDays"/> changed. <see cref="EnqueueAsync"/> only records the request; the
/// work itself happens in <see cref="ProcessAsync"/>, which the worker drives, so the endpoint that starts a
/// reapply returns immediately.
/// </summary>
public sealed class RetentionReapplyService(
    IRetentionReapplyRequestRepository requests,
    IRetentionPolicyRepository policies,
    TimeProvider timeProvider,
    ILogger<RetentionReapplyService> logger)
{
    /// <summary>Documents touched per call to <see cref="IRetentionReapplyRequestRepository.ReapplyBatchAsync"/>.</summary>
    public const int BatchSize = 100;

    public async Task<RetentionReapplyRequest> EnqueueAsync(Guid retentionPolicyId, CancellationToken ct)
    {
        var policy = await policies.FindByIdAsync(retentionPolicyId, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("retention-policy", retentionPolicyId.ToString());

        var now = timeProvider.GetUtcNow();
        var request = RetentionReapplyRequest.Create(Guid.CreateVersion7(now), policy.Id, now);

        await requests.AddAsync(request, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Retention reapply requested. retentionReapplyRequestId={RetentionReapplyRequestId} retentionPolicyId={RetentionPolicyId}",
            request.Id,
            policy.Id);

        return request;
    }

    /// <summary>
    /// Runs one reapply request to completion: batches through the documents of its policy until none are left,
    /// then marks it completed. Callable directly, without a running worker loop, which is what makes it testable.
    /// </summary>
    public async Task ProcessAsync(Guid requestId, CancellationToken ct)
    {
        var request = await requests.FindByIdAsync(requestId, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("retention-reapply-request", requestId.ToString());

        if (request.Status == RetentionReapplyStatus.Pending)
        {
            request.MarkRunning(timeProvider.GetUtcNow());
            await requests.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var policy = await policies.FindByIdAsync(request.RetentionPolicyId, ct).ConfigureAwait(false)
                ?? throw new ResourceNotFoundException("retention-policy", request.RetentionPolicyId.ToString());

            var total = 0;
            int updated;
            do
            {
                updated = await requests.ReapplyBatchAsync(policy, BatchSize, timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
                total += updated;
            }
            while (updated > 0);

            // The batch work runs each document in its own transaction and may clear the underlying change
            // tracker between them (see the EF implementation), which would silently detach this entity: reload
            // it before mutating so the completion actually persists.
            var completedRequest = await requests.FindByIdAsync(requestId, ct).ConfigureAwait(false) ?? request;
            completedRequest.MarkCompleted(timeProvider.GetUtcNow(), total);
            await requests.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogInformation(
                "Retention reapply completed. retentionReapplyRequestId={RetentionReapplyRequestId} retentionPolicyId={RetentionPolicyId} documentsUpdated={DocumentsUpdated}",
                completedRequest.Id,
                completedRequest.RetentionPolicyId,
                total);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failedRequest = await requests.FindByIdAsync(requestId, ct).ConfigureAwait(false) ?? request;
            failedRequest.MarkFailed(timeProvider.GetUtcNow(), exception.Message);
            await requests.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogError(
                exception,
                "Retention reapply failed. retentionReapplyRequestId={RetentionReapplyRequestId} retentionPolicyId={RetentionPolicyId} errorType={ErrorType}",
                failedRequest.Id,
                failedRequest.RetentionPolicyId,
                exception.GetType().Name);
        }
    }
}
