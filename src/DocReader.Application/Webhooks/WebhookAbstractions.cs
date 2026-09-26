using DocReader.Application.Documents;
using DocReader.Domain.Webhooks;

namespace DocReader.Application.Webhooks;

/// <summary>Filters of the subscription listing.</summary>
/// <param name="Active">Only active (true) or inactive (false) subscriptions.</param>
/// <param name="ProductServiceId">Only subscriptions for this product or service.</param>
/// <param name="Page">One based page number.</param>
/// <param name="PageSize">Items per page.</param>
public sealed record WebhookSubscriptionFilter(bool? Active, Guid? ProductServiceId, int Page, int PageSize);

/// <summary>Persistence of the webhook subscriptions. Finds by id return tracked entities, so a change is saved with <see cref="SaveChangesAsync"/>.</summary>
public interface IWebhookSubscriptionRepository
{
    Task<WebhookSubscription?> FindByIdAsync(Guid id, CancellationToken ct);

    Task<PagedResult<WebhookSubscription>> ListAsync(WebhookSubscriptionFilter filter, CancellationToken ct);

    Task AddAsync(WebhookSubscription subscription, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>Removes the subscription; its pending deliveries go with it.</summary>
    Task RemoveAsync(WebhookSubscription subscription, CancellationToken ct);

    /// <summary>The most recent deliveries of a subscription, newest first.</summary>
    Task<PagedResult<WebhookDelivery>> ListDeliveriesAsync(Guid subscriptionId, int page, int pageSize, CancellationToken ct);
}

/// <summary>What the dispatcher needs to send a delivery it has taken.</summary>
/// <param name="DeliveryId">The delivery.</param>
/// <param name="SubscriptionId">Its subscription.</param>
/// <param name="DocumentId">The document the notification is about.</param>
/// <param name="Event">Event name.</param>
/// <param name="Payload">The frozen JSON body.</param>
/// <param name="Attempt">The attempt this is, from 1.</param>
/// <param name="Url">Where to send it.</param>
/// <param name="EncryptedSecret">The subscription's signing secret, encrypted.</param>
/// <param name="SubscriptionActive">False when the subscription was deactivated after the notification was queued.</param>
public sealed record ClaimedDelivery(
    Guid DeliveryId,
    Guid SubscriptionId,
    Guid DocumentId,
    string Event,
    string Payload,
    int Attempt,
    string Url,
    string EncryptedSecret,
    bool SubscriptionActive);

/// <summary>The queue of notifications. Every write is fenced by the worker and the attempt, so a worker that lost its delivery changes nothing.</summary>
public interface IWebhookDeliveryStore
{
    /// <summary>
    /// Takes the next delivery that is due (pending, its time reached, not held by a live worker) and starts its attempt. Two workers
    /// never take the same one.
    /// </summary>
    Task<ClaimedDelivery?> ClaimNextAsync(DateTimeOffset now, TimeSpan lockTimeout, string worker, CancellationToken ct);

    Task<bool> RecordSuccessAsync(Guid deliveryId, string worker, int attempt, int statusCode, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Records a failed attempt. With <paramref name="nextAttemptAt"/> the delivery is retried then; without it, this was the last attempt:
    /// the delivery fails for good and the document timeline gets a <c>WEBHOOK_DELIVERY_FAILED</c> event, in the same transaction.
    /// </summary>
    Task<bool> RecordFailureAsync(
        Guid deliveryId,
        string worker,
        int attempt,
        int? statusCode,
        string errorCode,
        DateTimeOffset now,
        DateTimeOffset? nextAttemptAt,
        CancellationToken ct);

    Task<bool> CancelAsync(Guid deliveryId, string worker, int attempt, DateTimeOffset now, CancellationToken ct);
}

/// <summary>One signed POST to a subscriber.</summary>
/// <param name="Url">Destination.</param>
/// <param name="Body">The exact bytes to send, the ones the signature covers.</param>
/// <param name="Signature">Value of <c>X-Webhook-Signature</c>.</param>
/// <param name="Event">Value of <c>X-Webhook-Event</c>.</param>
/// <param name="DeliveryId">Value of <c>X-Webhook-Delivery</c>, the same across retries so a receiver can deduplicate.</param>
/// <param name="Attempt">Value of <c>X-Webhook-Attempt</c>.</param>
public sealed record WebhookRequest(string Url, byte[] Body, string Signature, string Event, Guid DeliveryId, int Attempt);

/// <summary>What came of a POST: the HTTP status when the subscriber answered, or a code when it did not.</summary>
/// <param name="StatusCode">HTTP status of the answer, null when there was none.</param>
/// <param name="ErrorCode">TIMEOUT, CONNECTION_FAILED or BLOCKED_ADDRESS when there was no answer; null otherwise.</param>
public sealed record WebhookSendResult(int? StatusCode, string? ErrorCode)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>The code recorded when the attempt failed: <c>HTTP_503</c> for an answer, the transport code otherwise.</summary>
    public string FailureCode => ErrorCode ?? (StatusCode is { } status ? $"HTTP_{status}" : "UNKNOWN");
}

/// <summary>Sends the POST. The dispatcher decides what to do with the result.</summary>
public interface IWebhookSender
{
    Task<WebhookSendResult> SendAsync(WebhookRequest request, CancellationToken ct);
}
