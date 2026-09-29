using DocReader.Application.GdprDeletion;

namespace DocReader.Worker;

/// <summary>
/// Drives a GDPR/LGPD deletion request to completion: auto-approves requests nobody decided on within the
/// configured window, then executes approved requests one at a time, polling for more work when there is none.
/// The same shape as <see cref="RetentionReapplyWorker"/>, simpler because there is a single document to act on
/// per request rather than a batch, and no partial-run state to resume: <see cref="GdprDeletionExecutionService"/>
/// re-locks and rechecks every request it touches, so a restart loses nothing.
/// </summary>
public sealed class GdprDeletionWorker(
    IServiceScopeFactory scopes,
    ILogger<GdprDeletionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var execution = scope.ServiceProvider.GetRequiredService<GdprDeletionExecutionService>();

                if (await execution.AutoApprovePastDueAsync(stoppingToken).ConfigureAwait(false) > 0)
                {
                    didWork = true;
                }

                if (await execution.ExecuteNextApprovedAsync(stoppingToken).ConfigureAwait(false))
                {
                    didWork = true;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A broken request or a database blip must not stop the loop: it is logged and the loop tries again later.
                logger.LogError(exception, "GDPR deletion run failed. errorType={ErrorType}", exception.GetType().Name);
            }

            if (didWork)
            {
                continue;
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
