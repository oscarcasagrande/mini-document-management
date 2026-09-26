namespace DocReader.Domain.Webhooks;

/// <summary>Where a notification is on its way to a subscriber.</summary>
public enum WebhookDeliveryStatus
{
    /// <summary>Waiting for its first attempt or for a retry (also while a worker is sending it).</summary>
    Pending = 0,

    /// <summary>The subscriber answered with a 2xx.</summary>
    Succeeded = 1,

    /// <summary>Every attempt failed. The document timeline says so.</summary>
    Failed = 2,

    /// <summary>The subscription was deactivated before the notification was sent, so it was dropped.</summary>
    Cancelled = 3
}
