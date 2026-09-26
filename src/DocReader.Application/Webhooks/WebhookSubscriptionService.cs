using System.Security.Cryptography;
using DocReader.Application.Abstractions;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Application.Options;
using DocReader.Domain.Webhooks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocReader.Application.Webhooks;

/// <summary>A subscription that was just created, with the secret if the service had to make one up.</summary>
/// <param name="Subscription">The subscription.</param>
/// <param name="GeneratedSecret">The secret, when none was given. This is the only time it exists in plain text: it is not stored that way and no endpoint returns it again.</param>
public sealed record CreatedWebhookSubscription(WebhookSubscription Subscription, string? GeneratedSecret);

/// <summary>Administration of the webhook subscriptions. The secret is encrypted before it is stored and never read back out through the API.</summary>
public sealed class WebhookSubscriptionService(
    IWebhookSubscriptionRepository repository,
    IProductServiceRepository productServices,
    ISecretProtector protector,
    IOptions<WebhookOptions> options,
    TimeProvider timeProvider,
    ILogger<WebhookSubscriptionService> logger)
{
    public Task<PagedResult<WebhookSubscription>> ListAsync(WebhookSubscriptionFilter filter, CancellationToken ct) =>
        repository.ListAsync(filter, ct);

    public async Task<WebhookSubscription> GetAsync(Guid id, CancellationToken ct) =>
        await repository.FindByIdAsync(id, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("webhook-subscription", id.ToString());

    public async Task<CreatedWebhookSubscription> CreateAsync(
        string? url,
        string? secret,
        IReadOnlyList<string>? events,
        Guid? productServiceId,
        bool active,
        CancellationToken ct)
    {
        var validUrl = WebhookUrlPolicy.Validate(url, options.Value.AllowPrivateNetworks);
        var validEvents = ValidateEvents(events);
        await EnsureProductServiceAsync(productServiceId, ct).ConfigureAwait(false);

        var generated = string.IsNullOrWhiteSpace(secret) ? GenerateSecret() : null;
        var effective = generated ?? ValidateSecret(secret!);

        var now = timeProvider.GetUtcNow();
        var subscription = WebhookSubscription.Create(
            Guid.CreateVersion7(now), validUrl, protector.Protect(effective), validEvents, productServiceId, active, now);

        await repository.AddAsync(subscription, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Webhook subscription created. webhookSubscriptionId={WebhookSubscriptionId} events={Events} productServiceId={ProductServiceId}",
            subscription.Id,
            string.Join(',', subscription.Events),
            subscription.ProductServiceId);

        return new CreatedWebhookSubscription(subscription, generated);
    }

    /// <summary>Replaces the URL, the events, the product filter and the active flag. A <paramref name="secret"/> rotates the signing secret; without one the current one stays.</summary>
    public async Task<WebhookSubscription> UpdateAsync(
        Guid id,
        string? url,
        string? secret,
        IReadOnlyList<string>? events,
        Guid? productServiceId,
        bool active,
        CancellationToken ct)
    {
        var subscription = await GetAsync(id, ct).ConfigureAwait(false);

        var validUrl = WebhookUrlPolicy.Validate(url, options.Value.AllowPrivateNetworks);
        var validEvents = ValidateEvents(events);
        await EnsureProductServiceAsync(productServiceId, ct).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        subscription.Update(validUrl, validEvents, productServiceId, active, now);

        if (!string.IsNullOrWhiteSpace(secret))
        {
            subscription.ReplaceSecret(protector.Protect(ValidateSecret(secret)), now);
        }

        await repository.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Webhook subscription updated. webhookSubscriptionId={WebhookSubscriptionId} active={Active} secretRotated={SecretRotated}",
            subscription.Id,
            subscription.Active,
            !string.IsNullOrWhiteSpace(secret));

        return subscription;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var subscription = await GetAsync(id, ct).ConfigureAwait(false);

        await repository.RemoveAsync(subscription, ct).ConfigureAwait(false);

        logger.LogInformation("Webhook subscription deleted. webhookSubscriptionId={WebhookSubscriptionId}", id);
    }

    public async Task<PagedResult<WebhookDelivery>> ListDeliveriesAsync(Guid id, int page, int pageSize, CancellationToken ct)
    {
        await GetAsync(id, ct).ConfigureAwait(false);

        return await repository.ListDeliveriesAsync(id, page, pageSize, ct).ConfigureAwait(false);
    }

    private static string[] ValidateEvents(IReadOnlyList<string>? events)
    {
        if (events is null || events.Count == 0)
        {
            throw new RequestValidationException(
                "INVALID_WEBHOOK_EVENTS",
                $"Subscribe to at least one event: {string.Join(", ", WebhookEvents.All)}.");
        }

        var unknown = events.Where(name => !WebhookEvents.IsValid(name?.Trim())).ToArray();
        if (unknown.Length > 0)
        {
            throw new RequestValidationException(
                "INVALID_WEBHOOK_EVENTS",
                $"Unknown event(s): {string.Join(", ", unknown)}. Known events: {string.Join(", ", WebhookEvents.All)}.");
        }

        return [.. events.Select(name => name.Trim()).Distinct(StringComparer.Ordinal)];
    }

    private static string ValidateSecret(string secret)
    {
        var trimmed = secret.Trim();

        return trimmed.Length is >= WebhookSubscription.MinimumSecretLength and <= WebhookSubscription.MaximumSecretLength
            ? trimmed
            : throw new RequestValidationException(
                "INVALID_WEBHOOK_SECRET",
                $"The secret must have {WebhookSubscription.MinimumSecretLength} to {WebhookSubscription.MaximumSecretLength} characters. Leave it out to get one generated.");
    }

    private static string GenerateSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private async Task EnsureProductServiceAsync(Guid? productServiceId, CancellationToken ct)
    {
        if (productServiceId is { } id && await productServices.FindByIdAsync(id, ct).ConfigureAwait(false) is null)
        {
            throw new UnprocessableRequestException(
                "PRODUCT_SERVICE_NOT_FOUND",
                $"There is no product or service with the id {id}.");
        }
    }
}
