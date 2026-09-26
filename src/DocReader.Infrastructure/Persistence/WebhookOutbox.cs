using DocReader.Application.Webhooks;
using DocReader.Domain.Documents;
using DocReader.Domain.Webhooks;
using Microsoft.EntityFrameworkCore;

namespace DocReader.Infrastructure.Persistence;

/// <summary>
/// Queues the notifications of a document that just reached a final state. It only adds rows to the context: the caller saves them
/// in the same transaction that changed the document, so a notification exists if and only if the change does (a transactional
/// outbox). One row per active subscription that asked for the event and whose product filter fits.
/// </summary>
internal static class WebhookOutbox
{
    public static async Task EnqueueAsync(DocReaderDbContext dbContext, Document document, DateTimeOffset now, CancellationToken ct)
    {
        var webhookEvent = WebhookPayload.EventFor(document.Status);
        if (webhookEvent is null)
        {
            return;
        }

        var productServiceId = document.ProductServiceId;

        var subscriptions = await dbContext.WebhookSubscriptions
            .AsNoTracking()
            .Where(subscription => subscription.Active
                && subscription.Events.Contains(webhookEvent)
                && (subscription.ProductServiceId == null || subscription.ProductServiceId == productServiceId))
            .Select(subscription => subscription.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (subscriptions.Count == 0)
        {
            return;
        }

        var productServiceCode = productServiceId is { } id
            ? await dbContext.ProductServices.AsNoTracking()
                .Where(product => product.Id == id)
                .Select(product => product.Code)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false)
            : null;

        // One body for all: it says what happened, not who is told.
        var payload = WebhookPayload.Build(webhookEvent, document, productServiceCode, now);

        foreach (var subscriptionId in subscriptions)
        {
            dbContext.WebhookDeliveries.Add(WebhookDelivery.Create(Guid.CreateVersion7(now), subscriptionId, document.Id, webhookEvent, payload, now));
        }
    }
}
