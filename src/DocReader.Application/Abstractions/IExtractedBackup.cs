using DocReader.Application.Backup;

namespace DocReader.Application.Abstractions;

/// <summary>A verified backup archive, extracted to temporary storage. Disposing it removes the extracted files.</summary>
public interface IExtractedBackup : IAsyncDisposable
{
    /// <summary>The documents of the storage manifest.</summary>
    IReadOnlyList<BackupManifestEntry> Documents { get; }

    /// <summary>Local path of the plain SQL dump, for <see cref="IDatabaseRestorer.RestoreAsync"/>.</summary>
    string DatabaseDumpPath { get; }

    /// <summary>Opens a document file copied into the archive, by its <see cref="BackupManifestEntry.ArchivePath"/>.</summary>
    Task<Stream> OpenArchivedFileAsync(string archivePath, CancellationToken ct);
}
