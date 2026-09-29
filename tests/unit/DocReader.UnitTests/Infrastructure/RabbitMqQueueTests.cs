using System.Linq;
using DocReader.Application.Options;
using DocReader.Application.Processing;
using DocReader.Infrastructure.Queue;
using DocReader.Infrastructure.Queue.RabbitMq;
using Xunit;

namespace DocReader.UnitTests.Infrastructure;

/// <summary>
/// Pure, broker-free coverage of ADR 0004's RabbitMQ plumbing: provider selection and the per-attempt
/// delay-queue TTL computation. The live broker round trip, the duplicate-delivery idempotency guard and
/// the DLX retry timing live in the integration suite (need a real RabbitMQ).
/// </summary>
public sealed class QueueProviderOptionsTests
{
    [Fact]
    public void Ausente_ou_vazio_escolhe_postgres()
    {
        Assert.Equal(QueueProvider.Postgres, QueueProviderOptions.FromConfigurationValue(null).Provider);
        Assert.Equal(QueueProvider.Postgres, QueueProviderOptions.FromConfigurationValue(string.Empty).Provider);
        Assert.Equal(QueueProvider.Postgres, QueueProviderOptions.FromConfigurationValue("   ").Provider);
    }

    [Theory]
    [InlineData("Postgres")]
    [InlineData("postgres")]
    [InlineData("POSTGRES")]
    public void Aceita_postgres_em_qualquer_caixa(string raw)
    {
        Assert.Equal(QueueProvider.Postgres, QueueProviderOptions.FromConfigurationValue(raw).Provider);
    }

    [Theory]
    [InlineData("RabbitMq")]
    [InlineData("RabbitMQ")]
    [InlineData("rabbitmq")]
    public void Aceita_rabbitmq_em_qualquer_caixa(string raw)
    {
        Assert.Equal(QueueProvider.RabbitMq, QueueProviderOptions.FromConfigurationValue(raw).Provider);
    }

    [Fact]
    public void Valor_desconhecido_lanca()
    {
        Assert.Throws<InvalidOperationException>(() => QueueProviderOptions.FromConfigurationValue("Kafka"));
    }
}

public sealed class RabbitMqTopologyTests
{
    private static ProcessingQueueOptions Options(int maxAttempts, int baseSeconds = 15, int maxSeconds = 300) =>
        new()
        {
            MaxAttempts = maxAttempts,
            RetryBaseDelay = TimeSpan.FromSeconds(baseSeconds),
            RetryMaxDelay = TimeSpan.FromSeconds(maxSeconds)
        };

    [Fact]
    public void Uma_fila_de_atraso_por_tentativa_que_ainda_pode_repetir()
    {
        // MaxAttempts=3 (the compose default): only attempts 1 and 2 can still retry, so exactly two
        // delay queues, matching CLAUDE.md's own worked example (15s then 30s).
        var delayQueues = RabbitMqTopology.DelayQueues("docreader.processing.jobs", Options(maxAttempts: 3));

        Assert.Equal(2, delayQueues.Count);
        Assert.Equal(1, delayQueues[0].AttemptNumber);
        Assert.Equal("docreader.processing.jobs.retry.1", delayQueues[0].QueueName);
        Assert.Equal(2, delayQueues[1].AttemptNumber);
        Assert.Equal("docreader.processing.jobs.retry.2", delayQueues[1].QueueName);
    }

    [Theory]
    [InlineData(1, 15_000)]
    [InlineData(2, 30_000)]
    [InlineData(3, 60_000)]
    public void Ttl_da_fila_de_atraso_bate_exatamente_com_RetryBackoff_For(int attemptNumber, long expectedTtlMilliseconds)
    {
        var options = Options(maxAttempts: 10);

        var delayQueues = RabbitMqTopology.DelayQueues("docreader.processing.jobs", options);
        var entry = delayQueues.Single(candidate => candidate.AttemptNumber == attemptNumber);

        Assert.Equal(expectedTtlMilliseconds, entry.TtlMilliseconds);
        Assert.Equal((long)RetryBackoff.For(attemptNumber, options).TotalMilliseconds, entry.TtlMilliseconds);
    }

    [Fact]
    public void Ttl_satura_no_teto_igual_ao_RetryBackoff()
    {
        var options = Options(maxAttempts: 10, baseSeconds: 15, maxSeconds: 60);

        var delayQueues = RabbitMqTopology.DelayQueues("docreader.processing.jobs", options);
        var lastEntry = delayQueues[^1];

        Assert.Equal(60_000, lastEntry.TtlMilliseconds);
    }

    [Fact]
    public void Uma_unica_tentativa_maxima_nao_declara_fila_de_atraso_alguma()
    {
        // MaxAttempts=1: nothing ever retries, so there is no attempt number left to build a delay
        // queue for - a definitive failure just acks the original delivery.
        var delayQueues = RabbitMqTopology.DelayQueues("docreader.processing.jobs", Options(maxAttempts: 1));

        Assert.Empty(delayQueues);
    }

    [Fact]
    public void Consumer_timeout_fica_acima_do_processing_timeout_com_a_margem_configurada()
    {
        var options = Options(maxAttempts: 3);
        options.ProcessingTimeout = TimeSpan.FromMinutes(60);
        var margin = TimeSpan.FromMinutes(5);

        var consumerTimeoutMs = RabbitMqTopology.ConsumerTimeoutMilliseconds(options, margin);

        Assert.Equal((long)TimeSpan.FromMinutes(65).TotalMilliseconds, consumerTimeoutMs);
    }

    [Fact]
    public void Nome_da_fila_de_atraso_segue_o_padrao_fila_principal_retry_tentativa()
    {
        Assert.Equal("docreader.processing.jobs.retry.1", RabbitMqTopology.RetryQueueName("docreader.processing.jobs", 1));
        Assert.Equal("docreader.processing.jobs.retry.2", RabbitMqTopology.RetryQueueName("docreader.processing.jobs", 2));
    }
}
