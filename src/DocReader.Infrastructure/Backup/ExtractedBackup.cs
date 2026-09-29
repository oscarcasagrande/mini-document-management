using DocReader.Application.Abstractions;
using DocReader.Application.Backup;

namespace DocReader.Infrastructure.Backup;

/// <summary>A verified, extracted archive in its workspace; disposing it removes the workspace.</summary>
public sealed class ExtractedBackup(
    BackupWorkspace workspace,
    string root,
    IReadOnlyList<BackupManifestEntry> documents,
    IReadOnlySet<string> verifiedPaths) : IExtractedBackup
{
    public IReadOnlyList<BackupManifestEntry> Documents { get; } = documents;

    public string DatabaseDumpPath { get; } = Path.Combine(root, BackupArchiveLayout.DatabaseDump);

    public Task<Stream> OpenArchivedFileAsync(string archivePath, CancellationToken ct)
    {
        // Only a path whose checksum was verified can be opened; that also rules out anything outside the root.
        if (!verifiedPaths.Contains(archivePath))
        {
            throw new FileNotFoundException("The archive has no verified file at this path.", archivePath);
        }

        Stream stream = new FileStream(
            Path.Combine(root, archivePath.Replace('/', Path.DirectorySeparatorChar)),
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });

        return Task.FromResult(stream);
    }

    public ValueTask DisposeAsync() => workspace.DisposeAsync();
}
