using DocReader.Application.Abstractions;
using DocReader.Application.Backup;

namespace DocReader.Worker;

/// <summary>
/// Picks up backup jobs one at a time and runs them to completion, polling for the next one when none is pending. A
/// job interrupted by a stop is marked failed by <see cref="BackupService.ProcessAsync"/>; request a new one.
/// </summary>
public sealed class BackupWorker(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<BackupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<IBackupJobRepository>();

                var claimed = await repository.ClaimNextPendingAsync(timeProvider.GetUtcNow(), stoppingToken).ConfigureAwait(false);

                if (claimed is not null)
                {
                    var service = scope.ServiceProvider.GetRequiredService<BackupService>();
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
                // A broken job or a database blip must not stop the loop: it is logged and the loop tries again later.
                logger.LogError(exception, "Backup run failed. errorType={ErrorType}", exception.GetType().Name);
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
