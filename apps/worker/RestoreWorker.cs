using DocReader.Application.Abstractions;
using DocReader.Application.Backup;

namespace DocReader.Worker;

/// <summary>
/// Picks up restore jobs one at a time and runs them to completion, polling for the next one when none is pending. On
/// start it first recovers from a restore a previous run of the worker left behind (<see cref="RestoreService.RecoverInterruptedAsync"/>):
/// the job is marked failed and the read-only gate lowered, so a crash never leaves the API refusing writes forever.
/// </summary>
public sealed class RestoreWorker(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<RestoreWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var recovered = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();

                if (!recovered)
                {
                    await scope.ServiceProvider.GetRequiredService<RestoreService>().RecoverInterruptedAsync(stoppingToken).ConfigureAwait(false);
                    recovered = true;
                }

                var repository = scope.ServiceProvider.GetRequiredService<IRestoreJobRepository>();
                var claimed = await repository.ClaimNextPendingAsync(timeProvider.GetUtcNow(), stoppingToken).ConfigureAwait(false);

                if (claimed is not null)
                {
                    var service = scope.ServiceProvider.GetRequiredService<RestoreService>();
                    await service.ProcessAsync(claimed.Id, stoppingToken).ConfigureAwait(false);

                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Restore run failed. errorType={ErrorType}", exception.GetType().Name);
            }

            try
            {
                await Task.Delay(PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
