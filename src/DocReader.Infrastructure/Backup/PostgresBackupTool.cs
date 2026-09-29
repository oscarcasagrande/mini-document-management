using System.Data;
using System.Diagnostics;
using System.Globalization;
using DocReader.Application.Abstractions;
using DocReader.Application.Backup;
using DocReader.Application.Options;
using DocReader.Domain;
using DocReader.Domain.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DocReader.Infrastructure.Backup;

/// <summary>
/// The PostgreSQL client tools, pointed at the application's own database: <c>pg_dump</c> for a backup, <c>psql</c> for
/// a restore. The connection string is decomposed into discrete flags; the password goes only into the child's
/// <c>PGPASSWORD</c>, never on the command line (where <c>ps</c> would show it) nor in a log. stderr is summarized into
/// the exception message, keeping only error lines (psql runs with <c>VERBOSITY=terse</c>, so no DETAIL or CONTEXT line,
/// which is where row values would appear); the log gets the exit code only.
/// </summary>
public sealed class PostgresBackupTool : IDatabaseRestorer, IDatabaseDumper
{
    private const int MaxErrorLineLength = 300;
    private const int MaxErrorLines = 5;

    private readonly NpgsqlConnectionStringBuilder _connection;
    private readonly BackupOptions _options;
    private readonly ILogger<PostgresBackupTool> _logger;

    public PostgresBackupTool(string connectionString, IOptions<BackupOptions> options, ILogger<PostgresBackupTool> logger)
    {
        // Maintenance connections are opened fresh, never taken from the pool the application uses.
        _connection = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_connection.Host) || string.IsNullOrWhiteSpace(_connection.Database))
        {
            throw new InvalidOperationException("The connection string must name a host and a database for backup and restore.");
        }
    }

    /// <summary>
    /// Dumps the database to <paramref name="outputPath"/> as plain SQL and returns the storage manifest of the very same
    /// snapshot: a repeatable-read transaction exports its snapshot, the manifest is read inside it and <c>pg_dump</c>
    /// runs with <c>--snapshot</c>, so a document uploaded or deleted meanwhile is in both or in neither.
    /// </summary>
    public async Task<IReadOnlyList<BackupManifestEntry>> DumpAsync(string outputPath, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connection.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);

        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
        {
            await readOnly.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        string snapshot;
        await using (var export = new NpgsqlCommand("SELECT pg_export_snapshot()", connection, transaction))
        {
            snapshot = (string)(await export.ExecuteScalarAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("PostgreSQL did not export a snapshot."));
        }

        var manifest = await ReadManifestAsync(connection, transaction, ct).ConfigureAwait(false);

        List<string> arguments =
        [
            .. ConnectionArguments(),
            "--format=plain",
            "--clean",
            "--if-exists",
            "--no-owner",
            "--no-privileges",
            "--encoding=UTF8",
            "--snapshot=" + snapshot,
            "--file=" + outputPath
        ];
        arguments.AddRange(BackupArchiveLayout.ExcludedTables.Select(table => "--exclude-table=" + table));

        await RunAsync(_options.PgDumpPath, "pg_dump", arguments, ct).ConfigureAwait(false);

        await transaction.RollbackAsync(ct).ConfigureAwait(false);

        return manifest;
    }

    public async Task RestoreAsync(string databaseDumpPath, CancellationToken ct)
    {
        if (!File.Exists(databaseDumpPath))
        {
            throw new FileNotFoundException("The database dump to restore does not exist.", databaseDumpPath);
        }

        List<string> arguments =
        [
            .. ConnectionArguments(),
            "--no-psqlrc",
            "--quiet",
            // One transaction, stopped by the first error: PostgreSQL then rolls everything back.
            "--single-transaction",
            "--set=ON_ERROR_STOP=on",
            "--set=VERBOSITY=terse",
            "--file=" + databaseDumpPath
        ];

        await RunAsync(_options.PsqlPath, "psql", arguments, ct).ConfigureAwait(false);

        // Idle pooled connections were opened against the catalog before the restore: start over with fresh ones.
        NpgsqlConnection.ClearAllPools();
    }

    public async Task<int> CountMissingDocumentsAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken ct)
    {
        if (documentIds.Count == 0)
        {
            return 0;
        }

        await using var connection = new NpgsqlConnection(_connection.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)::int
            FROM unnest(@ids) AS manifest(id)
            WHERE NOT EXISTS (SELECT 1 FROM documents AS document WHERE document.id = manifest.id)
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", [.. documentIds]));

        return (int)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
    }

    private static async Task<IReadOnlyList<BackupManifestEntry>> ReadManifestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT document.id, document.storage_repository_id, document.storage_key, repository.provider,
                   document.mime_type, document.uploaded_at
            FROM documents AS document
            LEFT JOIN storage_repositories AS repository ON repository.id = document.storage_repository_id
            WHERE document.status <> 'PURGED'
            ORDER BY document.id
            """,
            connection,
            transaction);

        var entries = new List<BackupManifestEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            StorageProvider? provider = null;
            if (!reader.IsDBNull(3) && EnumNaming.TryParse<StorageProvider>(reader.GetString(3), out var parsed))
            {
                provider = parsed;
            }

            entries.Add(new BackupManifestEntry(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                provider,
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                ArchivePath: null));
        }

        return entries;
    }

    private IEnumerable<string> ConnectionArguments()
    {
        yield return "--host=" + _connection.Host;
        yield return "--port=" + _connection.Port.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(_connection.Username))
        {
            yield return "--username=" + _connection.Username;
        }

        yield return "--dbname=" + _connection.Database;
        yield return "--no-password";
    }

    private Dictionary<string, string> ChildEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PGAPPNAME"] = "docreader-backup",
            ["PGCONNECT_TIMEOUT"] = "15",
            ["PGCLIENTENCODING"] = "UTF8",
            ["PGSSLMODE"] = _connection.SslMode switch
            {
                SslMode.Disable => "disable",
                SslMode.Allow => "allow",
                SslMode.Require => "require",
                SslMode.VerifyCA => "verify-ca",
                SslMode.VerifyFull => "verify-full",
                _ => "prefer"
            }
        };

        if (!string.IsNullOrEmpty(_connection.Password))
        {
            environment["PGPASSWORD"] = _connection.Password;
        }

        return environment;
    }

    private async Task RunAsync(string executable, string tool, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();

        var result = await ExternalProcess
            .RunAsync(executable, arguments, ChildEnvironment(), _options.CommandTimeout, ct)
            .ConfigureAwait(false);

        var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (result.ExitCode == 0)
        {
            _logger.LogInformation("{Tool} finished. exitCode=0 durationMs={DurationMs}", tool, elapsed);
            return;
        }

        _logger.LogError("{Tool} failed. exitCode={ExitCode} durationMs={DurationMs}", tool, result.ExitCode, elapsed);

        throw new PostgresToolException(
            $"{tool} exited with code {result.ExitCode}. {Summarize(result.StandardErrorTail)}".TrimEnd());
    }

    /// <summary>Error lines only, each shortened: enough for an operator to see what failed, not a dump of the input.</summary>
    private static string Summarize(IReadOnlyList<string> lines)
    {
        var errors = lines
            .Where(line =>
                line.Contains("ERROR:", StringComparison.Ordinal) ||
                line.Contains("FATAL:", StringComparison.Ordinal) ||
                line.Contains("error:", StringComparison.Ordinal) ||
                line.Contains("fatal:", StringComparison.Ordinal) ||
                line.Contains(": detail:", StringComparison.Ordinal))
            .Select(line => line.Length <= MaxErrorLineLength ? line : line[..MaxErrorLineLength] + "...")
            .Take(MaxErrorLines)
            .ToList();

        return errors.Count == 0 ? string.Empty : string.Join(" | ", errors);
    }
}
