using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using DocReader.Application.Processing;
using Microsoft.Extensions.Options;

namespace DocReader.Worker;

/// <summary>
/// The consumption loop of RF-007: reserve a job, run it, repeat. One job at a time per worker,
/// which is what the OCR service (one page at a time, ~3 GB) was sized for; more throughput means
/// more workers, and the queue's <c>FOR UPDATE SKIP LOCKED</c> keeps them from colliding.
///
/// Each job runs in its own scope, so the database context, the queue and the processor share
/// nothing with the previous job. Nothing that escapes the processor may stop the loop: an outage
/// of the database is logged and waited out.
/// </summary>
public sealed class ProcessingWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ProcessingQueueOptions> options,
    ILogger<ProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        logger.LogInformation(
            "Worker started. consumption=enabled worker={Worker} pollIntervalSeconds={PollInterval} jobLockTimeoutSeconds={LockTimeout} maxAttempts={MaxAttempts}",
            settings.WorkerName,
            settings.PollInterval.TotalSeconds,
            settings.JobLockTimeout.TotalSeconds,
            settings.MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = false;

            try
            {
                processed = await ProcessNextAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Database down, for instance. The health check reports it; the loop just waits.
                logger.LogError(
                    "The consumption loop failed and will try again. errorType={ErrorType}",
                    exception.GetType().Name);
            }

            if (processed)
            {
                continue;
            }

            try
            {
                await Task.Delay(settings.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Worker stopping.");
    }

    /// <summary>Returns true when a job was found, so the loop asks again at once.</summary>
    private async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        var queue = scope.ServiceProvider.GetRequiredService<IProcessingQueue>();
        var job = await queue.AcquireNextAsync(ct).ConfigureAwait(false);
        if (job is null)
        {
            return false;
        }

        var processor = scope.ServiceProvider.GetRequiredService<DocumentProcessor>();
        await processor.ProcessAsync(job, ct).ConfigureAwait(false);

        return true;
    }
}
