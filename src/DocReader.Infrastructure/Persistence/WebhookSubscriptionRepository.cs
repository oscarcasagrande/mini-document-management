using DocReader.Application.Documents;
using DocReader.Application.Webhooks;
using DocReader.Domain.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace DocReader.Infrastructure.Persistence;

public sealed class WebhookSubscriptionRepository(DocReaderDbContext dbContext) : IWebhookSubscriptionRepository
{
    public Task<WebhookSubscription?> FindByIdAsync(Guid id, CancellationToken ct) =>
        dbContext.WebhookSubscriptions.Include(subscription => subscription.ProductService)
            .FirstOrDefaultAsync(subscription => subscription.Id == id, ct);

    public async Task<PagedResult<WebhookSubscription>> ListAsync(WebhookSubscriptionFilter filter, CancellationToken ct)
    {
        var query = dbContext.WebhookSubscriptions.AsNoTracking().Include(subscription => subscription.ProductService).AsQueryable();

        if (filter.Active is { } active)
        {
            query = query.Where(subscription => subscription.Active == active);
        }

        if (filter.ProductServiceId is { } productServiceId)
        {
            query = query.Where(subscription => subscription.ProductServiceId == productServiceId);
        }

        var totalCount = await query.LongCountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(subscription => subscription.CreatedAt)
            .ThenByDescending(subscription => subscription.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<WebhookSubscription>(items, filter.Page, filter.PageSize, totalCount);
    }

    public async Task AddAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        dbContext.WebhookSubscriptions.Add(subscription);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        await dbContext.Entry(subscription).Reference(item => item.ProductService).LoadAsync(ct).ConfigureAwait(false);
    }

    public Task SaveChangesAsync(CancellationToken ct) => dbContext.SaveChangesAsync(ct);

    public async Task RemoveAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        dbContext.WebhookSubscriptions.Remove(subscription);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<PagedResult<WebhookDelivery>> ListDeliveriesAsync(Guid subscriptionId, int page, int pageSize, CancellationToken ct)
    {
        var query = dbContext.WebhookDeliveries.AsNoTracking().Where(delivery => delivery.SubscriptionId == subscriptionId);

        var totalCount = await query.LongCountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(delivery => delivery.CreatedAt)
            .ThenByDescending(delivery => delivery.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<WebhookDelivery>(items, page, pageSize, totalCount);
    }
}
