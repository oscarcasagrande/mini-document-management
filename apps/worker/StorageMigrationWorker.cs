using DocReader.Application.Abstractions;
using DocReader.Application.StorageMigrations;

namespace DocReader.Worker;

/// <summary>
/// Picks up storage migration jobs one at a time and runs them to their end, polling for the next one when none is
/// pending. A cancelled job is noticed before the next batch. <see cref="StorageMigrationService.ProcessAsync"/> can
/// resume a partially-completed run because the batches it selects skip documents already moved.
/// </summary>
public sealed class StorageMigrationWorker(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<StorageMigrationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<IStorageMigrationJobRepository>();

                var claimed = await repository.ClaimNextPendingAsync(timeProvider.GetUtcNow(), stoppingToken).ConfigureAwait(false);

                if (claimed is not null)
                {
                    var service = scope.ServiceProvider.GetRequiredService<StorageMigrationService>();
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
                logger.LogError(exception, "Storage migration run failed. errorType={ErrorType}", exception.GetType().Name);
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
