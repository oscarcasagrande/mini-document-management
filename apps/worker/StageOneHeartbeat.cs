namespace DocReader.Worker;

/// <summary>
/// Keeps the stub honest: it logs, at a low rate, that the worker is alive and not yet processing.
/// It exists so an operator reading the logs is never left guessing whether the worker is broken or
/// simply not implemented yet.
/// </summary>
public sealed class StageOneHeartbeat(ILogger<StageOneHeartbeat> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Worker started as a stage 1 stub. It answers health probes and does not consume processing_jobs yet.");

        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                logger.LogInformation("Worker heartbeat. stage=1 role=stub queueConsumption=disabled");
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Worker stopping.");
        }
    }
}
