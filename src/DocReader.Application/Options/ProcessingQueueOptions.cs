using System.ComponentModel.DataAnnotations;

namespace DocReader.Application.Options;

/// <summary>
/// Ajustes do consumo da fila, conforme RF-007 e as ADRs 0001 e 0002.
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
    /// Tempo sem heartbeat depois do qual se considera que o worker morreu e o job volta para
    /// PENDING. O worker renova a reserva a cada página lida, então isto mede o silêncio desde a
    /// última página e nunca a duração total do documento: um job longo que segue avançando não é
    /// pego por outro worker. Precisa ser maior que o tempo de uma única página
    /// (<see cref="OcrProviderOptions.PageTimeout"/>).
    /// </summary>
    public TimeSpan JobLockTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Tentativas totais antes de declarar falha definitiva.</summary>
    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Atraso da primeira retentativa; dobra a cada tentativa até o teto.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Teto absoluto de uma tentativa, do preparo à persistência. Não é o que detecta worker morto
    /// (isso é o heartbeat): existe só para que um documento gigante não ocupe o worker para sempre.
    /// </summary>
    public TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromMinutes(60);
}
