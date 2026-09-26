using DocReader.Application.Webhooks;
using DocReader.Domain.Documents;
using DocReader.Domain.Webhooks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace DocReader.Infrastructure.Persistence;

/// <summary>
/// The queue of webhook notifications, in PostgreSQL. A delivery is taken with <c>FOR UPDATE SKIP LOCKED</c>, so two workers
/// never take the same one; every later write checks that the delivery is still held by that worker at that attempt.
/// </summary>
public sealed class WebhookDeliveryStore(DocReaderDbContext dbContext) : IWebhookDeliveryStore
{
    private const string ClaimSql =
        """
        UPDATE webhook_deliveries
        SET attempt_count = attempt_count + 1,
            last_attempt_at = @now,
            locked_at = @now,
            locked_by = @worker
        WHERE id = (
            SELECT id
            FROM webhook_deliveries
            WHERE status = 'PENDING'
              AND next_attempt_at <= @now
              AND (locked_at IS NULL OR locked_at < @stale)
            ORDER BY next_attempt_at, id
            FOR UPDATE SKIP LOCKED
            LIMIT 1
        )
        RETURNING id;
        """;

    public async Task<ClaimedDelivery?> ClaimNextAsync(DateTimeOffset now, TimeSpan lockTimeout, string worker, CancellationToken ct)
    {
        var id = await ExecuteScalarGuidAsync(
            ClaimSql,
            [
                new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now },
                new NpgsqlParameter("stale", NpgsqlDbType.TimestampTz) { Value = now - lockTimeout },
                new NpgsqlParameter("worker", NpgsqlDbType.Varchar) { Value = worker }
            ],
            ct).ConfigureAwait(false);

        if (id is null)
        {
            return null;
        }

        dbContext.ChangeTracker.Clear();
        var delivery = await dbContext.WebhookDeliveries
            .AsNoTracking()
            .Include(item => item.Subscription)
            .FirstAsync(item => item.Id == id.Value, ct)
            .ConfigureAwait(false);

        var subscription = delivery.Subscription!;

        return new ClaimedDelivery(
            delivery.Id,
            delivery.SubscriptionId,
            delivery.DocumentId,
            delivery.Event,
            delivery.Payload,
            delivery.AttemptCount,
            subscription.Url,
            subscription.EncryptedSecret,
            subscription.Active);
    }

    public Task<bool> RecordSuccessAsync(Guid deliveryId, string worker, int attempt, int statusCode, DateTimeOffset now, CancellationToken ct) =>
        UpdateOwnedAsync(deliveryId, worker, attempt, (delivery, _) => delivery.RecordSuccess(statusCode, now), ct);

    public Task<bool> RecordFailureAsync(
        Guid deliveryId,
        string worker,
        int attempt,
        int? statusCode,
        string errorCode,
        DateTimeOffset now,
        DateTimeOffset? nextAttemptAt,
        CancellationToken ct) =>
        UpdateOwnedAsync(
            deliveryId,
            worker,
            attempt,
            (delivery, document) =>
            {
                delivery.RecordFailure(statusCode, errorCode, now, nextAttemptAt);

                // The last attempt: leave a trace where the document owner will look. The URL and the body stay out of it.
                if (delivery.Status == WebhookDeliveryStatus.Failed)
                {
                    document?.RecordProgress(
                        DocumentEventTypes.WebhookDeliveryFailed,
                        now,
                        $"event={delivery.Event} subscription={delivery.SubscriptionId} attempts={delivery.AttemptCount} error={errorCode}");
                }
            },
            ct);

    public Task<bool> CancelAsync(Guid deliveryId, string worker, int attempt, DateTimeOffset now, CancellationToken ct) =>
        UpdateOwnedAsync(deliveryId, worker, attempt, (delivery, _) => delivery.Cancel(now), ct);

    private async Task<bool> UpdateOwnedAsync(
        Guid deliveryId,
        string worker,
        int attempt,
        Action<WebhookDelivery, Document?> apply,
        CancellationToken ct)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async cancellationToken =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var delivery = await dbContext.WebhookDeliveries
                .FirstOrDefaultAsync(item => item.Id == deliveryId, cancellationToken)
                .ConfigureAwait(false);

            // The fence: only the worker that took this attempt may write its outcome.
            if (delivery is null
                || delivery.Status != WebhookDeliveryStatus.Pending
                || delivery.LockedBy != worker
                || delivery.AttemptCount != attempt)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return false;
            }

            var document = await dbContext.Documents
                .FirstOrDefaultAsync(item => item.Id == delivery.DocumentId, cancellationToken)
                .ConfigureAwait(false);

            apply(delivery, document);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return true;
        }, ct).ConfigureAwait(false);
    }

    private async Task<Guid?> ExecuteScalarGuidAsync(string sql, NpgsqlParameter[] parameters, CancellationToken ct)
    {
        await dbContext.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

        try
        {
            var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddRange(parameters);

            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is Guid id ? id : null;
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
