using DocReader.Application.Abstractions;

namespace DocReader.Infrastructure.Backup;

/// <summary>A built <c>.tar.gz</c> in its workspace; disposing it removes the workspace.</summary>
public sealed class BackupArchiveFile(
    BackupWorkspace workspace,
    string path,
    long sizeBytes,
    string checksumSha256,
    int documentCount,
    int filesArchived) : IBackupArchiveFile
{
    public long SizeBytes { get; } = sizeBytes;

    public string ChecksumSha256 { get; } = checksumSha256;

    public int DocumentCount { get; } = documentCount;

    public int FilesArchived { get; } = filesArchived;

    public Stream OpenRead() => new FileStream(
        path,
        new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });

    public ValueTask DisposeAsync() => workspace.DisposeAsync();
}
