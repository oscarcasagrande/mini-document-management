using System.ComponentModel.DataAnnotations;

namespace DocReader.Application.Options;

/// <summary>Schedule of the job that purges documents whose retention period ended (worker only).</summary>
public sealed class PurgeOptions
{
    public const string SectionName = "DocReader:Purge";

    public const string DefaultCron = "0 2 * * *";

    /// <summary>Five-field cron expression, evaluated in UTC. The default runs every day at 02:00.</summary>
    [Required]
    public string ScheduleCron { get; set; } = DefaultCron;

    /// <summary>Documents taken per query, so a large backlog is purged in pieces.</summary>
    [Range(1, 1000)]
    public int BatchSize { get; set; } = 100;
}
