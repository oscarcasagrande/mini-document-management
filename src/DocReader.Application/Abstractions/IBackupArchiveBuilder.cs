using DocReader.Application.Backup;

namespace DocReader.Application.Abstractions;

/// <summary>
/// Builds a backup archive: a consistent dump of the database, the storage manifest of the documents in that same
/// snapshot, the document files the caller hands over, a SHA-256 checksum of every one of them, and a
/// signature over the checksums, all in one <c>.tar.gz</c>.
/// </summary>
public interface IBackupArchiveBuilder
{
    /// <param name="openContent">
    /// Called once per document of the snapshot; returns the bytes to copy into the archive, or null to keep only its
    /// manifest entry. The builder disposes the stream.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IBackupArchiveFile> BuildAsync(
        Func<BackupManifestEntry, CancellationToken, Task<Stream?>> openContent,
        CancellationToken ct);
}
