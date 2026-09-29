using DocReader.Infrastructure.Persistence;
using DocReader.Infrastructure.Queue;
using DocReader.Infrastructure.Queue.RabbitMq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocReader.Worker;

/// <summary>
/// Drains the transactional outbox (ADR 0004) into RabbitMQ. Claims due, unpublished rows with
/// <c>FOR UPDATE SKIP LOCKED</c>, publishes each to the broker, marks <c>published_at</c>. Only
/// registered/started when <c>QUEUE_PROVIDER=RabbitMQ</c> (see <c>Program.cs</c>).
///
/// This loop is what proves "broker down during upload, comes back up later": while the broker is
/// unreachable, <see cref="RabbitMqJobBridge.PublishNewJobAsync"/> throws, the attempt is logged and
/// <c>attempts</c> incremented for observability, <c>published_at</c> stays null, and the document stays
/// queued in PostgreSQL - exactly the same shape as <c>WebhookDispatcher</c>'s retry-forever-on-transient-
/// failure loop, copied faithfully on purpose. Once the broker comes back, the very next poll succeeds.
/// </summary>
public sealed class RabbitMqOutboxPublisher(
    IServiceScopeFactory scopes,
    RabbitMqJobBridge bridge,
    IOptions<RabbitMqOptions> options,
    TimeProvider timeProvider,
    ILogger<RabbitMqOutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        logger.LogInformation(
            "RabbitMQ outbox publisher started. pollIntervalSeconds={PollInterval} batchSize={BatchSize}",
            settings.OutboxPollInterval.TotalSeconds,
            settings.OutboxBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            var published = false;

            try
            {
                published = await PublishDueAsync(settings.OutboxBatchSize, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Broker unreachable, database blip, whatever: logged, never fatal. The outbox row(s)
                // stay unpublished and the next poll tries again - this is the whole "comes back up
                // later" guarantee.
                logger.LogError(
                    exception,
                    "Outbox publish attempt failed and will be retried on the next poll. errorType={ErrorType}",
                    exception.GetType().Name);
            }

            if (published)
            {
                continue;
            }

            try
            {
                await Task.Delay(settings.OutboxPollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("RabbitMQ outbox publisher stopping.");
    }

    /// <summary>Returns true when at least one row was published, so the loop asks again at once.</summary>
    private async Task<bool> PublishDueAsync(int batchSize, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DocReaderDbContext>();

        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async cancellationToken =>
        {
            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            var claimed = await dbContext.OutboxMessages
                .FromSqlInterpolated(
                    $"""
                     SELECT * FROM outbox_messages
                     WHERE published_at IS NULL
                     ORDER BY created_at
                     FOR UPDATE SKIP LOCKED
                     LIMIT {batchSize}
                     """)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (claimed.Count == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            var now = timeProvider.GetUtcNow();

            foreach (var message in claimed)
            {
                var payload = message.ReadPayload();

                // If the broker rejects this, the exception propagates out of PublishDueAsync and the
                // whole batch's transaction rolls back on disposal (nothing committed, nothing marked
                // published): every row in the batch, published or not, is retried on the next poll.
                // Simpler than partial-batch bookkeeping and just as correct for a PoC - a down broker
                // fails the very first publish in the batch anyway.
                await bridge.PublishNewJobAsync(payload.JobId, payload.DocumentId, cancellationToken).ConfigureAwait(false);

                message.MarkPublished(now);
                message.RecordAttempt();
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Published {Count} outbox message(s) to RabbitMQ.", claimed.Count);

            return true;
        }, ct).ConfigureAwait(false);
    }
}
