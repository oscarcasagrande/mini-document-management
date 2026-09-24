using DocReader.Application.Options;
using DocReader.Application.Processing;
using Xunit;

namespace DocReader.UnitTests.Application;

public sealed class RetryBackoffTests
{
    private static ProcessingQueueOptions Options(int baseSeconds = 15, int maxSeconds = 300, int maxAttempts = 3) =>
        new()
        {
            RetryBaseDelay = TimeSpan.FromSeconds(baseSeconds),
            RetryMaxDelay = TimeSpan.FromSeconds(maxSeconds),
            MaxAttempts = maxAttempts
        };

    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(4, 120)]
    public void Dobra_a_espera_a_cada_tentativa(int attemptCount, int expectedSeconds)
    {
        var delay = RetryBackoff.For(attemptCount, Options());

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void Satura_no_teto_configurado()
    {
        var options = Options(baseSeconds: 15, maxSeconds: 300);

        Assert.Equal(TimeSpan.FromSeconds(300), RetryBackoff.For(10, options));
        Assert.Equal(TimeSpan.FromSeconds(300), RetryBackoff.For(50, options));
    }

    [Fact]
    public void Contagem_absurda_satura_em_vez_de_estourar()
    {
        // Sem travar o expoente, 2^int.MaxValue vira infinito e TimeSpan.FromSeconds lança.
        var delay = RetryBackoff.For(int.MaxValue, Options());

        Assert.Equal(TimeSpan.FromSeconds(300), delay);
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(-5, 15)]
    public void Tentativa_zero_ou_negativa_usa_a_espera_base(int attemptCount, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RetryBackoff.For(attemptCount, Options()));
    }

    [Theory]
    [InlineData(1, true, true)]
    [InlineData(2, true, true)]
    [InlineData(3, true, false)]
    [InlineData(4, true, false)]
    public void Repete_falha_transitoria_ate_esgotar_as_tentativas(int attemptCount, bool isTransient, bool expected)
    {
        Assert.Equal(expected, RetryBackoff.ShouldRetry(attemptCount, isTransient, Options(maxAttempts: 3)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Falha_definitiva_nunca_repete(int attemptCount)
    {
        Assert.False(RetryBackoff.ShouldRetry(attemptCount, isTransient: false, Options()));
    }
}
