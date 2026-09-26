using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocReader.Application.Webhooks;

/// <summary>
/// Sends one queued notification: takes the next due delivery, signs its body, POSTs it and records what came of it. A 2xx is
/// success. Anything else (another status, a timeout, a refused connection) waits for the next retry delay, and when the delays
/// run out the delivery fails for good and the document timeline says so.
///
/// Logs carry identifiers, the event, the attempt and a result code. Never the URL (it can carry a token), the body or the secret.
/// </summary>
public sealed class WebhookDispatchService(
    IWebhookDeliveryStore deliveries,
    IWebhookSender sender,
    ISecretProtector protector,
    IOptions<WebhookOptions> options,
    TimeProvider timeProvider,
    ILogger<WebhookDispatchService> logger)
{
    /// <returns>True when a delivery was handled (so there may be more), false when nothing was due.</returns>
    public async Task<bool> DispatchNextAsync(string worker, CancellationToken ct)
    {
        var settings = options.Value;
        var claimed = await deliveries
            .ClaimNextAsync(timeProvider.GetUtcNow(), settings.LockTimeout, worker, ct)
            .ConfigureAwait(false);

        if (claimed is null)
        {
            return false;
        }

        if (!claimed.SubscriptionActive)
        {
            await deliveries.CancelAsync(claimed.DeliveryId, worker, claimed.Attempt, timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
            logger.LogInformation(
                "Webhook delivery dropped: the subscription is inactive. deliveryId={DeliveryId} webhookSubscriptionId={WebhookSubscriptionId} event={Event}",
                claimed.DeliveryId,
                claimed.SubscriptionId,
                claimed.Event);

            return true;
        }

        var result = await SendAsync(claimed, ct).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();

        if (result.IsSuccess)
        {
            await deliveries.RecordSuccessAsync(claimed.DeliveryId, worker, claimed.Attempt, result.StatusCode!.Value, now, ct).ConfigureAwait(false);
            logger.LogInformation(
                "Webhook delivered. deliveryId={DeliveryId} webhookSubscriptionId={WebhookSubscriptionId} event={Event} attempt={Attempt} statusCode={StatusCode}",
                claimed.DeliveryId,
                claimed.SubscriptionId,
                claimed.Event,
                claimed.Attempt,
                result.StatusCode);

            return true;
        }

        var delays = settings.RetryDelays;
        DateTimeOffset? next = claimed.Attempt <= delays.Length ? now.Add(delays[claimed.Attempt - 1]) : null;

        await deliveries
            .RecordFailureAsync(claimed.DeliveryId, worker, claimed.Attempt, result.StatusCode, result.FailureCode, now, next, ct)
            .ConfigureAwait(false);

        if (next is null)
        {
            logger.LogError(
                "Webhook delivery failed for good. deliveryId={DeliveryId} webhookSubscriptionId={WebhookSubscriptionId} documentId={DocumentId} event={Event} attempts={Attempts} error={ErrorCode}",
                claimed.DeliveryId,
                claimed.SubscriptionId,
                claimed.DocumentId,
                claimed.Event,
                claimed.Attempt,
                result.FailureCode);
        }
        else
        {
            logger.LogWarning(
                "Webhook attempt failed, retry scheduled. deliveryId={DeliveryId} webhookSubscriptionId={WebhookSubscriptionId} event={Event} attempt={Attempt} error={ErrorCode} nextAttemptAt={NextAttemptAt:O}",
                claimed.DeliveryId,
                claimed.SubscriptionId,
                claimed.Event,
                claimed.Attempt,
                result.FailureCode,
                next);
        }

        return true;
    }

    private async Task<WebhookSendResult> SendAsync(ClaimedDelivery claimed, CancellationToken ct)
    {
        string secret;
        try
        {
            secret = protector.Unprotect(claimed.EncryptedSecret);
        }
        catch (InvalidOperationException)
        {
            // The signing secret cannot be read (another encryption key, damaged data): every retry would fail the same way.
            logger.LogError(
                "The signing secret of a webhook subscription cannot be decrypted. webhookSubscriptionId={WebhookSubscriptionId}",
                claimed.SubscriptionId);

            return new WebhookSendResult(null, "SECRET_UNREADABLE");
        }

        var body = System.Text.Encoding.UTF8.GetBytes(claimed.Payload);

        return await sender
            .SendAsync(
                new WebhookRequest(claimed.Url, body, WebhookSignature.Compute(secret, body), claimed.Event, claimed.DeliveryId, claimed.Attempt),
                ct)
            .ConfigureAwait(false);
    }
}
