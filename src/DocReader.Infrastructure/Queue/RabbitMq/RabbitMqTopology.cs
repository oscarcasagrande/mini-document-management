using DocReader.Application.Options;
using DocReader.Application.Processing;

namespace DocReader.Infrastructure.Queue.RabbitMq;

/// <summary>
/// Pure computation of the RabbitMQ topology names and per-attempt delay-queue TTLs (ADR 0004). Kept
/// free of any RabbitMQ.Client type so it is unit-testable without a broker.
///
/// One delay queue per possible pre-retry attempt number, each with a queue-level <c>x-message-ttl</c>
/// computed from the exact same <see cref="RetryBackoff.For"/> formula the PostgreSQL provider already
/// uses, and <c>x-dead-letter-exchange</c>/<c>x-dead-letter-routing-key</c> pointing straight back at the
/// main queue through the default exchange. A single retry queue with a per-message <c>expiration</c>
/// property was deliberately not used: classic RabbitMQ queues only dead-letter from the head of the
/// queue, so a short-TTL message stuck behind a longer-TTL one would not expire on time.
/// </summary>
public static class RabbitMqTopology
{
    /// <summary>Name of the per-attempt delay queue a failed job's retry is published to.</summary>
    public static string RetryQueueName(string mainQueueName, int attemptNumber) =>
        $"{mainQueueName}.retry.{attemptNumber}";

    /// <summary>
    /// One entry per attempt number that can still retry (1..MaxAttempts-1), with the queue's TTL in
    /// milliseconds, computed once at declare time from the same options the Postgres provider reads.
    /// </summary>
    public static IReadOnlyList<(int AttemptNumber, string QueueName, long TtlMilliseconds)> DelayQueues(
        string mainQueueName,
        ProcessingQueueOptions queueOptions)
    {
        var entries = new List<(int, string, long)>();

        for (var attempt = 1; attempt < queueOptions.MaxAttempts; attempt++)
        {
            var ttl = RetryBackoff.For(attempt, queueOptions);
            entries.Add((attempt, RetryQueueName(mainQueueName, attempt), (long)ttl.TotalMilliseconds));
        }

        return entries;
    }

    /// <summary>
    /// The main queue's <c>x-consumer-timeout</c>, generously above <see cref="ProcessingQueueOptions.ProcessingTimeout"/>
    /// (the real outer bound of one attempt) so a legitimately slow but healthy document is never killed
    /// by the broker mid-processing.
    /// </summary>
    public static long ConsumerTimeoutMilliseconds(ProcessingQueueOptions queueOptions, TimeSpan margin) =>
        (long)(queueOptions.ProcessingTimeout + margin).TotalMilliseconds;
}
