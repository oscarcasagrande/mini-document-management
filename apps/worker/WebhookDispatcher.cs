using DocReader.Application.Options;
using DocReader.Application.Webhooks;
using Microsoft.Extensions.Options;

namespace DocReader.Worker;

/// <summary>
/// Sends the queued webhook notifications. It takes deliveries that are due, one at a time per loop, and each loop sleeps for a
/// poll interval when nothing is due. The queue lives in PostgreSQL, so a restart loses nothing and several workers share the
/// work without sending the same notification twice (see <c>IWebhookDeliveryStore</c>).
/// </summary>
public sealed class WebhookDispatcher(
    IServiceScopeFactory scopes,
    IOptions<WebhookOptions> options,
    TimeProvider timeProvider,
    ILogger<WebhookDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        logger.LogInformation(
            "Webhook dispatcher started. concurrency={Concurrency} retryDelays={RetryDelays} allowPrivateNetworks={AllowPrivateNetworks}",
            settings.Concurrency,
            string.Join(',', settings.RetryDelays.Select(delay => delay.TotalSeconds + "s")),
            settings.AllowPrivateNetworks);

        var loops = Enumerable.Range(1, settings.Concurrency).Select(index => RunLoopAsync(index, stoppingToken));

        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    private async Task RunLoopAsync(int index, CancellationToken stoppingToken)
    {
        var worker = $"{Environment.MachineName}-{Environment.ProcessId}-webhook-{index}";
        var poll = options.Value.PollInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatch = scope.ServiceProvider.GetRequiredService<WebhookDispatchService>();

                if (await dispatch.DispatchNextAsync(worker, stoppingToken).ConfigureAwait(false))
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A broken delivery or a database blip must not stop the loop: it is logged and the loop tries again later.
                logger.LogError(exception, "Webhook dispatch failed. worker={Worker} errorType={ErrorType}", worker, exception.GetType().Name);
            }

            try
            {
                await Task.Delay(poll, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
