using DocReader.Application.Backup;
using DocReader.Domain.Storage;

namespace DocReader.Infrastructure.Backup;

/// <summary>One document of <c>storage_manifest.json</c>.</summary>
public sealed record StorageManifestFileEntry(
    Guid DocumentId,
    Guid StorageRepositoryId,
    string StorageKey,
    StorageProvider? StorageProvider,
    string MimeType,
    DateTimeOffset UploadedAt,
    string? ArchivePath)
{
    public static StorageManifestFileEntry From(BackupManifestEntry entry) => new(
        entry.DocumentId,
        entry.StorageRepositoryId,
        entry.StorageKey,
        entry.StorageProvider,
        entry.MimeType,
        entry.UploadedAt,
        entry.ArchivePath);

    public BackupManifestEntry ToEntry() => new(
        DocumentId,
        StorageRepositoryId,
        StorageKey,
        StorageProvider,
        MimeType,
        UploadedAt,
        ArchivePath);
}
