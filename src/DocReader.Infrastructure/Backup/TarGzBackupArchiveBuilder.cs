using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using DocReader.Application.Abstractions;
using DocReader.Application.Backup;
using DocReader.Application.Options;
using Microsoft.Extensions.Options;

namespace DocReader.Infrastructure.Backup;

/// <summary>
/// Builds the backup archive in a private workspace: dump and manifest (same snapshot), the document files, the
/// checksum list and its signature, then a <c>.tar.gz</c> of it all with <see cref="TarWriter"/> over a
/// <see cref="GZipStream"/>. The staging copy is removed as soon as the archive exists, so the disk holds both only
/// while it is being written.
/// </summary>
public sealed class TarGzBackupArchiveBuilder(
    IDatabaseDumper dumper,
    BackupSignature signature,
    IOptions<BackupOptions> options,
    TimeProvider timeProvider) : IBackupArchiveBuilder
{
    private const string ArchiveName = "backup.tar.gz";

    public async Task<IBackupArchiveFile> BuildAsync(
        Func<BackupManifestEntry, CancellationToken, Task<Stream?>> openContent,
        CancellationToken ct)
    {
        var workspace = BackupWorkspace.Create(options.Value);

        try
        {
            var staging = workspace.CreateDirectory("staging");

            var snapshot = await dumper.DumpAsync(Path.Combine(staging, BackupArchiveLayout.DatabaseDump), ct).ConfigureAwait(false);

            Directory.CreateDirectory(Path.Combine(staging, BackupArchiveLayout.FilesDirectory));
            var entries = new List<BackupManifestEntry>(snapshot.Count);
            var filesArchived = 0;

            foreach (var entry in snapshot)
            {
                var content = await openContent(entry, ct).ConfigureAwait(false);
                if (content is null)
                {
                    entries.Add(entry with { ArchivePath = null });
                    continue;
                }

                var archivePath = BackupArchiveLayout.FilePathFor(entry.DocumentId);
                await using (content)
                {
                    await using var destination = CreateFile(Path.Combine(staging, archivePath));
                    await content.CopyToAsync(destination, ct).ConfigureAwait(false);
                }

                entries.Add(entry with { ArchivePath = archivePath });
                filesArchived++;
            }

            var manifest = StorageManifestFile.From(timeProvider.GetUtcNow(), entries);
            await using (var manifestStream = CreateFile(Path.Combine(staging, BackupArchiveLayout.StorageManifest)))
            {
                await JsonSerializer.SerializeAsync(manifestStream, manifest, StorageManifestFile.SerializerOptions, ct).ConfigureAwait(false);
            }

            // Checksums of everything staged so far, written before the archive exists, so the archive alone proves itself.
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var relative in RelativeFiles(staging))
            {
                hashes[relative] = await ChecksumList.HashFileAsync(Path.Combine(staging, relative), ct).ConfigureAwait(false);
            }

            var checksums = ChecksumList.Format(hashes);
            await File.WriteAllBytesAsync(Path.Combine(staging, BackupArchiveLayout.Checksums), checksums, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, BackupArchiveLayout.ChecksumsSignature), signature.Sign(checksums), ct).ConfigureAwait(false);

            var archivePathOnDisk = Path.Combine(workspace.Path, ArchiveName);
            await WriteTarGzAsync(staging, archivePathOnDisk, ct).ConfigureAwait(false);
            Directory.Delete(staging, recursive: true);

            var size = new FileInfo(archivePathOnDisk).Length;
            var checksum = await ChecksumList.HashFileAsync(archivePathOnDisk, ct).ConfigureAwait(false);

            return new BackupArchiveFile(workspace, archivePathOnDisk, size, checksum, entries.Count, filesArchived);
        }
        catch
        {
            await workspace.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WriteTarGzAsync(string sourceDirectory, string destination, CancellationToken ct)
    {
        await using var file = CreateFile(destination);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);

        foreach (var relative in RelativeFiles(sourceDirectory))
        {
            await tar.WriteEntryAsync(Path.Combine(sourceDirectory, relative), relative, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Every file under <paramref name="directory"/>, as forward-slash relative paths, in a stable order.</summary>
    private static IEnumerable<string> RelativeFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal);

    private static FileStream CreateFile(string path) => new(
        path,
        new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        });
}
