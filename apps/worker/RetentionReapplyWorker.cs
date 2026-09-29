using DocReader.Application.Abstractions;
using DocReader.Application.Retention;

namespace DocReader.Worker;

/// <summary>
/// Picks up retention reapply requests one at a time and runs them to completion, polling for the next one when
/// none is pending. A restart loses nothing: the request stays claimed by whichever run last started it, and
/// <see cref="RetentionReapplyService.ProcessAsync"/> resumes a partially-completed run because the batch it
/// drives skips documents already at the new duration.
/// </summary>
public sealed class RetentionReapplyWorker(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<RetentionReapplyWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<IRetentionReapplyRequestRepository>();

                var claimed = await repository.ClaimNextPendingAsync(timeProvider.GetUtcNow(), stoppingToken).ConfigureAwait(false);

                if (claimed is not null)
                {
                    var service = scope.ServiceProvider.GetRequiredService<RetentionReapplyService>();
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
                // A broken request or a database blip must not stop the loop: it is logged and the loop tries again later.
                logger.LogError(exception, "Retention reapply run failed. errorType={ErrorType}", exception.GetType().Name);
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
