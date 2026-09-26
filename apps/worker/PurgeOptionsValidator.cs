using Cronos;
using DocReader.Application.Options;
using Microsoft.Extensions.Options;

namespace DocReader.Worker;

/// <summary>Refuses to start the worker with a purge schedule that is not a valid five-field cron expression.</summary>
public sealed class PurgeOptionsValidator : IValidateOptions<PurgeOptions>
{
    public ValidateOptionsResult Validate(string? name, PurgeOptions options)
    {
        if (CronExpression.TryParse(options.ScheduleCron, CronFormat.Standard, out _))
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            $"DocReader:Purge:ScheduleCron (PURGE_SCHEDULE_CRON) is not a valid five-field cron expression: '{options.ScheduleCron}'. Example: 0 2 * * * runs every day at 02:00 UTC.");
    }
}
