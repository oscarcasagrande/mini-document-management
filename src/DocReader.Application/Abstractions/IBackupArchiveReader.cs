namespace DocReader.Application.Abstractions;

/// <summary>Opens a backup archive and proves it is intact before anything uses it.</summary>
public interface IBackupArchiveReader
{
    /// <summary>
    /// Extracts the archive to temporary storage and checks it: the signature over <c>checksums.sha256</c>, the SHA-256
    /// of every file against it, no file missing and none unlisted, and a readable storage manifest.
    /// </summary>
    /// <exception cref="Errors.BackupArchiveInvalidException">The archive is unreadable, tampered with or incomplete.</exception>
    Task<IExtractedBackup> ExtractAndVerifyAsync(Stream archive, CancellationToken ct);
}
