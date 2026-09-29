namespace DocReader.Infrastructure.Queue;

/// <summary>
/// Which <see cref="DocReader.Application.Abstractions.IProcessingQueue"/> implementation is bound at
/// startup. PostgreSQL (ADR 0001) stays the default; RabbitMQ is an opt-in alternative (ADR 0004).
/// </summary>
public enum QueueProvider
{
    Postgres,
    RabbitMq
}

/// <summary>
/// The resolved provider, read once from <c>DocReader:Queue:Provider</c> (<c>QUEUE_PROVIDER</c>) at DI
/// registration time and kept as a plain singleton instance rather than an <c>IOptions&lt;T&gt;</c>: it
/// never changes at runtime, and nothing in <c>DocReader.Application</c> needs to see it, so it does not
/// live on <see cref="DocReader.Application.Options.ProcessingQueueOptions"/>.
/// </summary>
public sealed record QueueProviderOptions(QueueProvider Provider)
{
    public const string ConfigurationKey = "DocReader:Queue:Provider";

    public static QueueProviderOptions FromConfigurationValue(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new QueueProviderOptions(QueueProvider.Postgres);
        }

        // "RabbitMQ" (the env var's natural casing) does not match the enum member "RabbitMq" case
        // sensitively; case-insensitive parsing keeps both spellings working.
        if (!Enum.TryParse<QueueProvider>(rawValue, ignoreCase: true, out var provider))
        {
            throw new InvalidOperationException(
                $"Unknown {ConfigurationKey} value '{rawValue}'. Expected 'Postgres' or 'RabbitMQ'.");
        }

        return new QueueProviderOptions(provider);
    }
}
