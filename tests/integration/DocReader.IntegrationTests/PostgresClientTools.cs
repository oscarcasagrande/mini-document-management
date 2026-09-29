using DocReader.Application.Options;
using DocReader.Infrastructure.Backup;

namespace DocReader.IntegrationTests;

/// <summary>
/// Finds <c>pg_dump</c> and <c>psql</c> for the backup and restore tests: the PGDG install path of major 17 first (what the
/// worker image uses), then PATH. The tests that need them are skipped when they are missing, like the database tests are
/// skipped without a database. The .NET SDK image does not ship them: <see cref="BackupRestoreTests"/> documents the command that installs them.
/// </summary>
public static class PostgresClientTools
{
    private const string PgdgBin = "/usr/lib/postgresql/17/bin";

    private static readonly Lazy<Task<string?>> Probe = new(ProbeAsync);

    public static string PgDump => File.Exists(Path.Combine(PgdgBin, "pg_dump")) ? Path.Combine(PgdgBin, "pg_dump") : "pg_dump";

    public static string Psql => File.Exists(Path.Combine(PgdgBin, "psql")) ? Path.Combine(PgdgBin, "psql") : "psql";

    /// <summary>Null when both tools run; otherwise why the tests are skipped.</summary>
    public static Task<string?> SkipReasonAsync() => Probe.Value;

    public static BackupOptions Options(string workingDirectory) => new()
    {
        WorkingDirectory = workingDirectory,
        PgDumpPath = PgDump,
        PsqlPath = Psql,
        CommandTimeout = TimeSpan.FromMinutes(5)
    };

    private static async Task<string?> ProbeAsync()
    {
        foreach (var tool in new[] { PgDump, Psql })
        {
            try
            {
                var result = await ExternalProcess.RunAsync(tool, ["--version"], new Dictionary<string, string>(), TimeSpan.FromSeconds(30), CancellationToken.None);
                if (result.ExitCode != 0)
                {
                    return $"{tool} --version exited with {result.ExitCode}.";
                }
            }
            catch (PostgresToolException)
            {
                return $"{Path.GetFileName(tool)} is not installed. Run the suite in an image with postgresql-client-17.";
            }
        }

        return null;
    }
}
