using DocReader.Domain.Storage;

namespace DocReader.Application.Backup;

/// <summary>
/// One document as the storage manifest of a backup records it: where its file lives and, when the file itself was
/// copied into the archive, under which path. Never carries document content.
/// </summary>
/// <param name="DocumentId">Identity of the document.</param>
/// <param name="StorageRepositoryId">Repository the file is stored in.</param>
/// <param name="StorageKey">Key of the file in that repository.</param>
/// <param name="StorageProvider">Provider of the repository when the backup was taken; null if it could not be resolved.</param>
/// <param name="MimeType">Detected MIME type of the file, needed to write it back.</param>
/// <param name="UploadedAt">Upload instant, which is what the storage key layout was derived from.</param>
/// <param name="ArchivePath">Path of the copied file inside the archive, or null when only the manifest entry was kept (cloud repositories, or a missing file).</param>
public sealed record BackupManifestEntry(
    Guid DocumentId,
    Guid StorageRepositoryId,
    string StorageKey,
    StorageProvider? StorageProvider,
    string MimeType,
    DateTimeOffset UploadedAt,
    string? ArchivePath)
{
    /// <summary>Whether the backup copies the file bytes: only for repositories that live with this installation.</summary>
    public bool IsLocallyStored => StorageProvider is Domain.Storage.StorageProvider.FileSystem or Domain.Storage.StorageProvider.Database;
}
