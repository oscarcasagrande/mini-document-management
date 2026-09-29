using RabbitMQ.Client;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// Real RabbitMQ for the ADR 0004 integration tests, in the same "skip cleanly, never fail, when the
/// resource is not reachable" spirit as <see cref="PostgresFixture"/>.
///
///   docker compose -f docker-compose.yml -f docker-compose.rabbitmq.yml up -d rabbitmq
///   bash scripts/dotnet.sh test tests/integration/DocReader.IntegrationTests
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    public string HostName { get; } = Environment.GetEnvironmentVariable("DOCREADER_TEST_RABBITMQ_HOST") ?? "localhost";

    public int Port { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("DOCREADER_TEST_RABBITMQ_PORT"), out var port) ? port : 5672;

    public string UserName { get; } = Environment.GetEnvironmentVariable("DOCREADER_TEST_RABBITMQ_USERNAME") ?? "docreader";

    public string Password { get; } = Environment.GetEnvironmentVariable("DOCREADER_TEST_RABBITMQ_PASSWORD") ?? "docreader";

    /// <summary>Null when reachable; the tests skip in that case instead of failing.</summary>
    public string? SkipReason { get; private set; }

    public bool Available => SkipReason is null;

    public async ValueTask InitializeAsync()
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = HostName,
                Port = Port,
                UserName = UserName,
                Password = Password
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var connection = await factory.CreateConnectionAsync(cts.Token);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);
        }
        catch (Exception exception)
        {
            SkipReason =
                $"RabbitMQ não alcançável em {HostName}:{Port}. Suba com " +
                "docker compose -f docker-compose.yml -f docker-compose.rabbitmq.yml up -d rabbitmq. " +
                $"Detalhe: {exception.GetType().Name}";
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[CollectionDefinition(nameof(RabbitMqCollection))]
public sealed class RabbitMqCollection : ICollectionFixture<RabbitMqFixture>, ICollectionFixture<PostgresFixture>;
