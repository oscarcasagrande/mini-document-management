using System.Text.Json;
using System.Threading.Channels;
using DocReader.Application.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace DocReader.Infrastructure.Queue.RabbitMq;

/// <summary>One delivery still waiting for <c>AcquireNextAsync</c> to turn it into a <see cref="Domain.Processing.ProcessingJob"/>.</summary>
internal sealed record RabbitMqDelivery(Guid JobId, Guid DocumentId, ulong DeliveryTag);

/// <summary>
/// Owns the one RabbitMQ connection and its two channels for the whole worker process (ADR 0004).
///
/// <c>IProcessingQueue</c> is registered <c>Scoped</c>, and <c>ProcessingWorker</c> opens a brand new DI
/// scope per job iteration; if the scoped queue implementation owned the connection/consumer itself, a
/// connection and a <c>basic.consume</c> subscription would open and tear down on every single job. This
/// type is the <c>Singleton</c> that is started once, subscribes once with the configured prefetch, and
/// bridges each broker delivery into a bounded <see cref="System.Threading.Channels.Channel{T}"/> that
/// the scoped <c>RabbitMqProcessingQueue.AcquireNextAsync</c> reads from. Ack/nack/publish all go through
/// this type too, since delivery tags are channel-scoped and acking must happen on the exact channel that
/// delivered the message.
/// </summary>
public sealed class RabbitMqJobBridge(
    IOptions<RabbitMqOptions> rabbitMqOptions,
    IOptions<ProcessingQueueOptions> queueOptions,
    ILogger<RabbitMqJobBridge> logger) : IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly RabbitMqOptions _rabbitMqOptions = rabbitMqOptions.Value;
    private readonly ProcessingQueueOptions _queueOptions = queueOptions.Value;
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly Channel<RabbitMqDelivery> _deliveries = Channel.CreateBounded<RabbitMqDelivery>(
        new BoundedChannelOptions(Math.Max(1, (int)rabbitMqOptions.Value.PrefetchCount)) { FullMode = BoundedChannelFullMode.Wait });

    private IConnection? _connection;
    private IChannel? _consumeChannel;
    private IChannel? _publishChannel;
    private IReadOnlyDictionary<int, string> _delayQueueNames = new Dictionary<int, string>();

    // Internal on purpose: RabbitMqDelivery (and this reader) are only meant for RabbitMqProcessingQueue,
    // in the same assembly. Everything apps/worker touches on this type (AckAsync, NackRequeueAsync,
    // PublishNewJobAsync, PublishRetryAsync) is public and does not leak the delivery-tag plumbing.
    internal ChannelReader<RabbitMqDelivery> Deliveries => _deliveries.Reader;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _rabbitMqOptions.HostName,
            Port = _rabbitMqOptions.Port,
            UserName = _rabbitMqOptions.UserName,
            Password = _rabbitMqOptions.Password,
            VirtualHost = _rabbitMqOptions.VirtualHost,
            AutomaticRecoveryEnabled = true
        };

        _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        _consumeChannel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        _publishChannel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        await DeclareTopologyAsync(cancellationToken).ConfigureAwait(false);

        await _consumeChannel.BasicQosAsync(0, _rabbitMqOptions.PrefetchCount, false, cancellationToken).ConfigureAwait(false);

        var consumer = new AsyncEventingBasicConsumer(_consumeChannel);
        consumer.ReceivedAsync += OnDeliveredAsync;

        await _consumeChannel.BasicConsumeAsync(
            queue: _rabbitMqOptions.QueueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "RabbitMQ job bridge started. host={Host} queue={Queue} prefetch={Prefetch} delayQueues={DelayQueueCount}",
            _rabbitMqOptions.HostName,
            _rabbitMqOptions.QueueName,
            _rabbitMqOptions.PrefetchCount,
            _delayQueueNames.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _deliveries.Writer.TryComplete();
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_consumeChannel is not null)
        {
            await _consumeChannel.DisposeAsync().ConfigureAwait(false);
        }

        if (_publishChannel is not null)
        {
            await _publishChannel.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _publishLock.Dispose();
    }

    public Task AckAsync(ulong deliveryTag, CancellationToken ct) =>
        _consumeChannel is null
            ? Task.CompletedTask
            : _consumeChannel.BasicAckAsync(deliveryTag, multiple: false, cancellationToken: ct).AsTask();

    public Task NackRequeueAsync(ulong deliveryTag, CancellationToken ct) =>
        _consumeChannel is null
            ? Task.CompletedTask
            : _consumeChannel.BasicNackAsync(deliveryTag, multiple: false, requeue: true, cancellationToken: ct).AsTask();

    /// <summary>Publishes a brand-new job announcement straight to the main processing queue.</summary>
    public Task PublishNewJobAsync(Guid jobId, Guid documentId, CancellationToken ct) =>
        PublishAsync(_rabbitMqOptions.QueueName, jobId, documentId, ct);

    /// <summary>
    /// Publishes a retry to the delay queue matching the attempt that just failed; it dead-letters back
    /// into the main queue once its <c>x-message-ttl</c> elapses.
    /// </summary>
    public Task PublishRetryAsync(int attemptNumber, Guid jobId, Guid documentId, CancellationToken ct)
    {
        if (!_delayQueueNames.TryGetValue(attemptNumber, out var queueName))
        {
            throw new InvalidOperationException(
                $"No delay queue was declared for attempt {attemptNumber}. DocReader:Queue:MaxAttempts may have " +
                "changed without the bridge being restarted to redeclare the topology.");
        }

        return PublishAsync(queueName, jobId, documentId, ct);
    }

    private async Task PublishAsync(string queueName, Guid jobId, Guid documentId, CancellationToken ct)
    {
        if (_publishChannel is null)
        {
            throw new InvalidOperationException("The RabbitMQ job bridge has not started.");
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(new OutboxJobPayload(jobId, documentId), Json);
        var properties = new BasicProperties { DeliveryMode = DeliveryModes.Persistent };

        await _publishLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _publishChannel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: queueName,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private async Task DeclareTopologyAsync(CancellationToken ct)
    {
        var consumerTimeoutMs = RabbitMqTopology.ConsumerTimeoutMilliseconds(_queueOptions, _rabbitMqOptions.ConsumerTimeoutMargin);

        await _consumeChannel!.QueueDeclareAsync(
            queue: _rabbitMqOptions.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-consumer-timeout"] = consumerTimeoutMs },
            cancellationToken: ct).ConfigureAwait(false);

        var delayQueues = RabbitMqTopology.DelayQueues(_rabbitMqOptions.QueueName, _queueOptions);
        var names = new Dictionary<int, string>();

        foreach (var (attemptNumber, queueName, ttlMilliseconds) in delayQueues)
        {
            await _consumeChannel.QueueDeclareAsync(
                queue: queueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-message-ttl"] = ttlMilliseconds,
                    // Empty exchange name = the default exchange, which routes by queue name: an expired
                    // delay-queue message dead-letters straight back into the main queue, no second
                    // exchange needed.
                    ["x-dead-letter-exchange"] = string.Empty,
                    ["x-dead-letter-routing-key"] = _rabbitMqOptions.QueueName
                },
                cancellationToken: ct).ConfigureAwait(false);

            names[attemptNumber] = queueName;
        }

        _delayQueueNames = names;
    }

    private async Task OnDeliveredAsync(object sender, BasicDeliverEventArgs ea)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<OutboxJobPayload>(ea.Body.Span, Json)
                ?? throw new JsonException("Empty payload.");

            await _deliveries.Writer
                .WriteAsync(new RabbitMqDelivery(payload.JobId, payload.DocumentId, ea.DeliveryTag), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A message this service itself never produced (or one from an old, incompatible payload
            // shape): logged without content, and rejected without requeue so it does not spin forever.
            logger.LogError(
                exception,
                "Unreadable delivery on the processing queue; rejecting without requeue. errorType={ErrorType}",
                exception.GetType().Name);

            if (_consumeChannel is not null)
            {
                await _consumeChannel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }
}
