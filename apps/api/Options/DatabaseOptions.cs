namespace DocReader.Api.Options;

/// <summary>
/// How migrations are applied. They are always explicit: nothing in the code calls EnsureCreated.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "DocReader:Database";

    /// <summary>
    /// When true the API applies pending migrations at start. The compose file turns it on so
    /// <c>docker compose up</c> is enough; the default is off so no environment migrates by accident.
    /// </summary>
    public bool RunMigrationsOnStartup { get; set; }

    /// <summary>Attempts to reach the database while applying migrations at start.</summary>
    public int MigrationAttempts { get; set; } = 10;

    /// <summary>Delay between migration attempts.</summary>
    public TimeSpan MigrationRetryDelay { get; set; } = TimeSpan.FromSeconds(3);
}
