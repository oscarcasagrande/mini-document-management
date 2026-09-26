using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using DocReader.Domain.Webhooks;
using Microsoft.AspNetCore.Mvc;

namespace DocReader.Api.Contracts.V1;

/// <summary>A subscriber that is told when a document reaches a final state. The signing secret is never returned.</summary>
/// <param name="Id">Identity.</param>
/// <param name="Url">Where the notifications are POSTed.</param>
/// <param name="Events">Events it receives: <c>document.completed</c>, <c>document.failed</c>, <c>document.purged</c>.</param>
/// <param name="ProductService">When set, only documents of this product or service are notified; null means every document.</param>
/// <param name="Active">An inactive subscription receives nothing, and what was still queued for it is dropped.</param>
/// <param name="CreatedAt">Creation instant, in UTC.</param>
/// <param name="UpdatedAt">Last change, in UTC.</param>
public sealed record WebhookSubscriptionResponse(
    Guid Id,
    string Url,
    IReadOnlyList<string> Events,
    ProductServiceReferenceResponse? ProductService,
    bool Active,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The answer to creating a subscription: the subscription and, when the secret was generated, the secret itself, this once.</summary>
/// <param name="Id">Identity.</param>
/// <param name="Url">Where the notifications are POSTed.</param>
/// <param name="Events">Events it receives.</param>
/// <param name="ProductService">Product or service filter, or null for every document.</param>
/// <param name="Active">Whether it receives notifications.</param>
/// <param name="CreatedAt">Creation instant, in UTC.</param>
/// <param name="UpdatedAt">Last change, in UTC.</param>
/// <param name="Secret">The signing secret, only when none was sent in the request and one had to be generated. Save it now: it is stored encrypted and no endpoint returns it again. Null when you supplied your own.</param>
public sealed record WebhookSubscriptionCreatedResponse(
    Guid Id,
    string Url,
    IReadOnlyList<string> Events,
    ProductServiceReferenceResponse? ProductService,
    bool Active,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Secret);

/// <summary>One notification and how its sending is going.</summary>
/// <param name="Id">Identity of the delivery; it is the <c>X-Webhook-Delivery</c> header, the same on every retry.</param>
/// <param name="DocumentId">The document it is about.</param>
/// <param name="Event">Event name.</param>
/// <param name="Status">PENDING, SUCCEEDED, FAILED or CANCELLED.</param>
/// <param name="AttemptCount">Attempts started so far.</param>
/// <param name="NextAttemptAt">When the next attempt is due (meaningful while PENDING).</param>
/// <param name="LastAttemptAt">When the last attempt started.</param>
/// <param name="LastStatusCode">HTTP status the subscriber answered with on the last attempt.</param>
/// <param name="LastError">Why the last attempt failed: HTTP_503, TIMEOUT, CONNECTION_FAILED, BLOCKED_ADDRESS.</param>
/// <param name="CreatedAt">When it was queued, in UTC.</param>
/// <param name="CompletedAt">When it succeeded, failed for good or was dropped, in UTC.</param>
public sealed record WebhookDeliveryResponse(
    Guid Id,
    Guid DocumentId,
    string Event,
    WebhookDeliveryStatus Status,
    int AttemptCount,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? LastAttemptAt,
    int? LastStatusCode,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed class CreateWebhookSubscriptionRequest
{
    /// <summary>Absolute http or https URL, with no credentials in it. Private, loopback and link-local addresses are refused unless the deployment allows them.</summary>
    [Required]
    [MaxLength(WebhookSubscription.MaxUrlLength)]
    public string? Url { get; init; }

    /// <summary>The HMAC-SHA256 key, 16 to 256 characters. Leave it out to have one generated; it is then returned once, in this answer.</summary>
    [MaxLength(WebhookSubscription.MaximumSecretLength)]
    public string? Secret { get; init; }

    /// <summary>One or more of <c>document.completed</c>, <c>document.failed</c>, <c>document.purged</c>.</summary>
    [Required]
    [MinLength(1)]
    public string[]? Events { get; init; }

    /// <summary>Only notify documents of this product or service; omit for every document.</summary>
    public Guid? ProductServiceId { get; init; }

    [DefaultValue(true)]
    public bool Active { get; init; } = true;
}

public sealed class UpdateWebhookSubscriptionRequest
{
    /// <summary>Absolute http or https URL, with no credentials in it.</summary>
    [Required]
    [MaxLength(WebhookSubscription.MaxUrlLength)]
    public string? Url { get; init; }

    /// <summary>A new signing secret, 16 to 256 characters. Omit it to keep the current one; there is no way to read the current one.</summary>
    [MaxLength(WebhookSubscription.MaximumSecretLength)]
    public string? Secret { get; init; }

    /// <summary>One or more of <c>document.completed</c>, <c>document.failed</c>, <c>document.purged</c>. Replaces the current list.</summary>
    [Required]
    [MinLength(1)]
    public string[]? Events { get; init; }

    /// <summary>Only notify documents of this product or service; omit (null) for every document.</summary>
    public Guid? ProductServiceId { get; init; }

    public bool Active { get; init; } = true;
}

public sealed class WebhookSubscriptionListRequest
{
    [FromQuery(Name = "active")]
    public bool? Active { get; init; }

    [FromQuery(Name = "productServiceId")]
    public Guid? ProductServiceId { get; init; }

    [FromQuery(Name = "page")]
    [Range(1, int.MaxValue)]
    [DefaultValue(1)]
    public int Page { get; init; } = 1;

    [FromQuery(Name = "pageSize")]
    [Range(1, 500)]
    public int? PageSize { get; init; }
}
