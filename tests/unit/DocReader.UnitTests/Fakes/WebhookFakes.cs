using DocReader.Application.Abstractions;
using DocReader.Application.Documents;
using DocReader.Application.Webhooks;
using DocReader.Domain.Webhooks;

namespace DocReader.UnitTests.Fakes;

/// <summary>In-memory subscriptions, with the deliveries a test wants to see.</summary>
public sealed class InMemoryWebhookSubscriptionStore : IWebhookSubscriptionRepository
{
    public List<WebhookSubscription> Items { get; } = [];

    public List<WebhookDelivery> Deliveries { get; } = [];

    public Task<WebhookSubscription?> FindByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(item => item.Id == id));

    public Task<PagedResult<WebhookSubscription>> ListAsync(WebhookSubscriptionFilter filter, CancellationToken ct)
    {
        var query = Items.AsEnumerable();

        if (filter.Active is { } active)
        {
            query = query.Where(item => item.Active == active);
        }

        if (filter.ProductServiceId is { } product)
        {
            query = query.Where(item => item.ProductServiceId == product);
        }

        var ordered = query.OrderByDescending(item => item.CreatedAt).ToList();

        return Task.FromResult(new PagedResult<WebhookSubscription>(
            [.. ordered.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)], filter.Page, filter.PageSize, ordered.Count));
    }

    public Task AddAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        Items.Add(subscription);

        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

    public Task RemoveAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        Items.Remove(subscription);
        Deliveries.RemoveAll(delivery => delivery.SubscriptionId == subscription.Id);

        return Task.CompletedTask;
    }

    public Task<PagedResult<WebhookDelivery>> ListDeliveriesAsync(Guid subscriptionId, int page, int pageSize, CancellationToken ct)
    {
        var mine = Deliveries.Where(delivery => delivery.SubscriptionId == subscriptionId).ToList();

        return Task.FromResult(new PagedResult<WebhookDelivery>([.. mine.Skip((page - 1) * pageSize).Take(pageSize)], page, pageSize, mine.Count));
    }
}

/// <summary>A delivery queue the test scripts: what is claimed, and what the dispatcher records.</summary>
public sealed class ScriptedDeliveryStore : IWebhookDeliveryStore
{
    public Queue<ClaimedDelivery> ToClaim { get; } = [];

    public List<(Guid Id, int Attempt, int StatusCode)> Successes { get; } = [];

    public List<(Guid Id, int Attempt, int? StatusCode, string Error, DateTimeOffset? Next)> Failures { get; } = [];

    public List<Guid> Cancelled { get; } = [];

    public Task<ClaimedDelivery?> ClaimNextAsync(DateTimeOffset now, TimeSpan lockTimeout, string worker, CancellationToken ct) =>
        Task.FromResult(ToClaim.TryDequeue(out var claimed) ? claimed : null);

    public Task<bool> RecordSuccessAsync(Guid deliveryId, string worker, int attempt, int statusCode, DateTimeOffset now, CancellationToken ct)
    {
        Successes.Add((deliveryId, attempt, statusCode));

        return Task.FromResult(true);
    }

    public Task<bool> RecordFailureAsync(
        Guid deliveryId, string worker, int attempt, int? statusCode, string errorCode, DateTimeOffset now, DateTimeOffset? nextAttemptAt, CancellationToken ct)
    {
        Failures.Add((deliveryId, attempt, statusCode, errorCode, nextAttemptAt));

        return Task.FromResult(true);
    }

    public Task<bool> CancelAsync(Guid deliveryId, string worker, int attempt, DateTimeOffset now, CancellationToken ct)
    {
        Cancelled.Add(deliveryId);

        return Task.FromResult(true);
    }
}

/// <summary>A sender that answers what the test says and remembers every request it was asked to make.</summary>
public sealed class ScriptedSender(Func<WebhookRequest, WebhookSendResult> answer) : IWebhookSender
{
    public List<WebhookRequest> Requests { get; } = [];

    public Task<WebhookSendResult> SendAsync(WebhookRequest request, CancellationToken ct)
    {
        Requests.Add(request);

        return Task.FromResult(answer(request));
    }
}
