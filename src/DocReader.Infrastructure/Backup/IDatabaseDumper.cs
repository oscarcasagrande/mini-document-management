using DocReader.Application.Backup;

namespace DocReader.Infrastructure.Backup;

/// <summary>Writes a plain SQL dump of the database and returns the storage manifest of the same snapshot.</summary>
public interface IDatabaseDumper
{
    Task<IReadOnlyList<BackupManifestEntry>> DumpAsync(string outputPath, CancellationToken ct);
}
