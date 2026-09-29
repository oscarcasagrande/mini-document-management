namespace DocReader.Application.Abstractions;

/// <summary>A built backup archive in temporary storage. Disposing it removes the temporary files.</summary>
public interface IBackupArchiveFile : IAsyncDisposable
{
    long SizeBytes { get; }

    /// <summary>SHA-256 (hex) of the archive file.</summary>
    string ChecksumSha256 { get; }

    /// <summary>Documents listed in the storage manifest.</summary>
    int DocumentCount { get; }

    /// <summary>Document files copied into the archive.</summary>
    int FilesArchived { get; }

    Stream OpenRead();
}
