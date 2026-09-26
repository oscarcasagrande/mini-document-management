namespace DocReader.Domain.Webhooks;

/// <summary>
/// One notification for one subscriber: what to send (a frozen body, so every retry sends the same bytes) and how the
/// sending is going. Rows are written in the same transaction that changes the document, so a document that reached a
/// final state always has its notifications queued (a transactional outbox).
/// </summary>
public sealed class WebhookDelivery
{
    private WebhookDelivery()
    {
    }

    public Guid Id { get; private init; }

    public Guid SubscriptionId { get; private init; }

    public WebhookSubscription? Subscription { get; private set; }

    public Guid DocumentId { get; private init; }

    /// <summary>The event name, such as <c>document.completed</c>.</summary>
    public string Event { get; private init; } = string.Empty;

    /// <summary>The JSON body that is sent. It carries identifiers and status, never document content.</summary>
    public string Payload { get; private init; } = string.Empty;

    public WebhookDeliveryStatus Status { get; private set; }

    /// <summary>Attempts started so far, counting the one in progress.</summary>
    public int AttemptCount { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    /// <summary>HTTP status of the last attempt, when the subscriber answered at all.</summary>
    public int? LastStatusCode { get; private set; }

    /// <summary>Why the last attempt failed, as a code (HTTP_503, TIMEOUT, CONNECTION_FAILED): never text from the response.</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset? LockedAt { get; private set; }

    public string? LockedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>A worker takes the delivery and starts an attempt. The lock only stops another worker from sending it at the same time.</summary>
    public void StartAttempt(DateTimeOffset now, string worker)
    {
        AttemptCount++;
        LastAttemptAt = now;
        LockedAt = now;
        LockedBy = worker;
    }

    public void RecordSuccess(int statusCode, DateTimeOffset now)
    {
        Status = WebhookDeliveryStatus.Succeeded;
        LastStatusCode = statusCode;
        LastError = null;
        CompletedAt = now;
        Unlock();
    }

    /// <summary>
    /// The attempt failed. With a <paramref name="nextAttemptAt"/> the delivery waits for its retry; without one, that was the last
    /// attempt and the delivery is failed for good.
    /// </summary>
    public void RecordFailure(int? statusCode, string errorCode, DateTimeOffset now, DateTimeOffset? nextAttemptAt)
    {
        LastStatusCode = statusCode;
        LastError = errorCode;
        Unlock();

        if (nextAttemptAt is { } next)
        {
            NextAttemptAt = next;
            return;
        }

        Status = WebhookDeliveryStatus.Failed;
        CompletedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        Status = WebhookDeliveryStatus.Cancelled;
        CompletedAt = now;
        Unlock();
    }

    private void Unlock()
    {
        LockedAt = null;
        LockedBy = null;
    }

    public static WebhookDelivery Create(
        Guid id,
        Guid subscriptionId,
        Guid documentId,
        string webhookEvent,
        string payload,
        DateTimeOffset now) =>
        new()
        {
            Id = id,
            SubscriptionId = subscriptionId,
            DocumentId = documentId,
            Event = webhookEvent,
            Payload = payload,
            Status = WebhookDeliveryStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now
        };
}
