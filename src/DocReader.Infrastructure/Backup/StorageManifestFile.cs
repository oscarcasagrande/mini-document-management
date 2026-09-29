using System.Text.Json;
using System.Text.Json.Serialization;
using DocReader.Application.Backup;

namespace DocReader.Infrastructure.Backup;

/// <summary>The <c>storage_manifest.json</c> of a backup archive. Its shape is a file format: change it only with a new version.</summary>
public sealed record StorageManifestFile(
    int FormatVersion,
    DateTimeOffset CreatedAt,
    IReadOnlyList<StorageManifestFileEntry> Documents)
{
    public const int CurrentFormatVersion = 1;

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper) }
    };

    public static StorageManifestFile From(DateTimeOffset createdAt, IEnumerable<BackupManifestEntry> entries) =>
        new(CurrentFormatVersion, createdAt, [.. entries.Select(StorageManifestFileEntry.From)]);
}
