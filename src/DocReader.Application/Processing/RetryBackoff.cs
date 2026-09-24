using DocReader.Application.Options;

namespace DocReader.Application.Processing;

/// <summary>
/// Política de espera entre tentativas de um job (RF-007). Fica fora da implementação da fila para
/// poder ser testada sem banco.
/// </summary>
public static class RetryBackoff
{
    /// <summary>
    /// Espera antes da próxima tentativa, dobrando a cada falha e limitada pelo teto configurado.
    /// <paramref name="attemptCount"/> é a tentativa que acabou de falhar, contada a partir de 1.
    /// </summary>
    public static TimeSpan For(int attemptCount, ProcessingQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var exponent = Math.Max(0, attemptCount - 1);

        // Trava o expoente antes de elevar: sem isso, uma contagem absurda estoura para infinito e
        // TimeSpan.FromSeconds lança em vez de simplesmente saturar no teto.
        const int maxExponent = 32;
        var multiplier = Math.Pow(2, Math.Min(exponent, maxExponent));

        var seconds = options.RetryBaseDelay.TotalSeconds * multiplier;
        var capped = Math.Min(seconds, options.RetryMaxDelay.TotalSeconds);

        return TimeSpan.FromSeconds(Math.Max(0, capped));
    }

    /// <summary>True quando ainda há tentativa disponível para uma falha transitória.</summary>
    public static bool ShouldRetry(int attemptCount, bool isTransient, ProcessingQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return isTransient && attemptCount < options.MaxAttempts;
    }
}
