using System.ComponentModel.DataAnnotations;

namespace DocReader.Application.Options;

/// <summary>
/// Ajustes do consumo da fila, conforme RF-007 e a ADR 0001.
/// </summary>
public sealed class ProcessingQueueOptions
{
    public const string SectionName = "DocReader:Queue";

    /// <summary>
    /// Identifica quem reservou o job, gravado em <c>locked_by</c>. Em contêiner, o hostname já
    /// distingue réplicas.
    /// </summary>
    public string WorkerName { get; set; } = Environment.MachineName;

    /// <summary>Intervalo entre sondagens quando a fila está vazia.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Depois deste tempo com o job em RUNNING, considera-se que o worker morreu e o job volta para
    /// PENDING. É o que impede que um reinício perca trabalho.
    /// </summary>
    public TimeSpan JobLockTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Tentativas totais antes de declarar falha definitiva.</summary>
    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Atraso da primeira retentativa; dobra a cada tentativa até o teto.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Teto de tempo de processamento de um documento, do preparo à persistência.</summary>
    public TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
