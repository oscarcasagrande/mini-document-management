using Cronos;
using DocReader.Application.Options;
using DocReader.Application.Retention;
using Microsoft.Extensions.Options;

namespace DocReader.Worker;

/// <summary>
/// Purges the documents whose retention period ended, on the schedule of <c>PURGE_SCHEDULE_CRON</c> (default: every
/// day at 02:00 UTC). Idempotent, so it does not matter that two workers run it, or that it runs again after a
/// restart: <see cref="DocumentPurgeService"/> only acts on what is still eligible. It logs how many documents it
/// purged and never anything read from them.
/// </summary>
public sealed class PurgeExpiredDocumentsJob(
    IServiceScopeFactory scopes,
    IOptions<PurgeOptions> options,
    TimeProvider timeProvider,
    ILogger<PurgeExpiredDocumentsJob> logger) : BackgroundService
{
    /// <summary>Longest single wait, so a schedule far away is re-evaluated now and then (clock adjustments, long sleeps).</summary>
    private static readonly TimeSpan MaximumWait = TimeSpan.FromHours(1);

    private readonly CronExpression _schedule = CronExpression.Parse(options.Value.ScheduleCron, CronFormat.Standard);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var next = NextRun(timeProvider.GetUtcNow());
        logger.LogInformation(
            "Purge job scheduled. cron={Cron} nextRunAt={NextRunAt:O}",
            options.Value.ScheduleCron,
            next);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (next is null)
            {
                logger.LogWarning("The purge schedule has no future occurrence, so the job stops. cron={Cron}", options.Value.ScheduleCron);
                return;
            }

            var wait = next.Value - timeProvider.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait > MaximumWait ? MaximumWait : wait, timeProvider, stoppingToken).ConfigureAwait(false);

                if (timeProvider.GetUtcNow() < next.Value)
                {
                    continue;
                }
            }

            await RunOnceAsync(stoppingToken).ConfigureAwait(false);

            next = NextRun(timeProvider.GetUtcNow());
        }
    }

    /// <summary>One purge run in its own scope. A failure is logged and does not stop the schedule.</summary>
    public async Task<PurgeResult?> RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var purge = scope.ServiceProvider.GetRequiredService<DocumentPurgeService>();

            return await purge.PurgeExpiredAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Purge run failed. errorType={ErrorType}", exception.GetType().Name);

            return null;
        }
    }

    private DateTimeOffset? NextRun(DateTimeOffset from) => _schedule.GetNextOccurrence(from, TimeZoneInfo.Utc);
}
