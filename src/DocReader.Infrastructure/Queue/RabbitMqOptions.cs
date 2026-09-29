namespace DocReader.Infrastructure.Queue;

/// <summary>
/// Connection and topology settings for the RabbitMQ alternative queue provider (ADR 0004). Only read
/// and only connected to when <see cref="QueueProviderOptions.Provider"/> is
/// <see cref="QueueProvider.RabbitMq"/>; in PostgreSQL mode nothing here is touched.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "DocReader:RabbitMq";

    public string HostName { get; set; } = "rabbitmq";

    public int Port { get; set; } = 5672;

    public string UserName { get; set; } = "guest";

    public string Password { get; set; } = "guest";

    public string VirtualHost { get; set; } = "/";

    /// <summary>
    /// Main processing queue, consumed by <c>AcquireNextAsync</c>. Published to directly through the
    /// default exchange (routing key = queue name): a single point-to-point work queue needs no
    /// exchange of its own.
    /// </summary>
    public string QueueName { get; set; } = "docreader.processing.jobs";

    /// <summary>
    /// One unacked delivery per worker process, matching the Postgres provider's one-job-at-a-time
    /// worker loop (<c>ProcessingWorker</c>); more throughput means more worker replicas, not a bigger
    /// prefetch.
    /// </summary>
    public ushort PrefetchCount { get; set; } = 1;

    /// <summary>How often <c>RabbitMqOutboxPublisher</c> polls <c>outbox_messages</c> for unpublished rows.</summary>
    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Rows claimed per poll.</summary>
    public int OutboxBatchSize { get; set; } = 20;

    /// <summary>
    /// Margin added on top of <c>ProcessingQueueOptions.ProcessingTimeout</c> to compute the queue's
    /// <c>x-consumer-timeout</c> argument, so a legitimately slow but healthy attempt is never killed by
    /// the broker mid-processing (see ADR 0004). Generous on purpose: this is a broker-level safety net
    /// for a hung consumer, not a tight budget.
    /// </summary>
    public TimeSpan ConsumerTimeoutMargin { get; set; } = TimeSpan.FromMinutes(5);
}
