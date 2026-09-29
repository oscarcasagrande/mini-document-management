using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using DocReader.Application.Abstractions;
using DocReader.Application.Backup;
using DocReader.Application.Errors;
using DocReader.Application.Options;
using Microsoft.Extensions.Options;

namespace DocReader.Infrastructure.Backup;

/// <summary>
/// Extracts a backup archive with <see cref="TarReader"/> over a <see cref="GZipStream"/> and verifies it. Extraction is
/// entry by entry rather than <c>TarFile.ExtractToDirectory</c>, to be stricter than it: only regular files and
/// directories, only names that pass <see cref="BackupArchiveLayout.IsSafeRelativePath"/>, no duplicates, and a ceiling
/// on the total bytes. Verification then needs nothing but the archive itself: the signature over the checksum list, the
/// SHA-256 of every file against the list, nothing missing, nothing unlisted, and a readable manifest.
/// </summary>
public sealed class TarGzBackupArchiveReader(BackupSignature signature, IOptions<BackupOptions> options) : IBackupArchiveReader
{
    private const int MaxEntries = 1_000_000;

    public async Task<IExtractedBackup> ExtractAndVerifyAsync(Stream archive, CancellationToken ct)
    {
        var workspace = BackupWorkspace.Create(options.Value);

        try
        {
            var root = workspace.CreateDirectory("extracted");
            var extracted = await ExtractAsync(archive, root, ct).ConfigureAwait(false);
            var verified = await VerifyAsync(root, extracted, ct).ConfigureAwait(false);
            var documents = await ReadManifestAsync(root, verified, ct).ConfigureAwait(false);

            return new ExtractedBackup(workspace, root, documents, verified);
        }
        catch
        {
            await workspace.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<HashSet<string>> ExtractAsync(Stream archive, string root, CancellationToken ct)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;

        try
        {
            await using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
            await using var tar = new TarReader(gzip, leaveOpen: false);

            while (await tar.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
            {
                if (entry.EntryType is TarEntryType.Directory)
                {
                    continue;
                }

                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    throw Invalid("BACKUP_ARCHIVE_INVALID", $"The archive holds an entry of type {entry.EntryType}; only regular files are expected.");
                }

                var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
                if (!BackupArchiveLayout.IsSafeRelativePath(name))
                {
                    throw Invalid("BACKUP_ARCHIVE_INVALID", "The archive holds an entry with an unexpected name.");
                }

                if (!files.Add(name) || files.Count > MaxEntries)
                {
                    throw Invalid("BACKUP_ARCHIVE_INVALID", "The archive holds a duplicated entry or too many entries.");
                }

                total += entry.Length;
                if (total > options.Value.MaxExtractedBytes)
                {
                    throw Invalid(
                        "BACKUP_ARCHIVE_TOO_LARGE",
                        $"The archive expands to more than {options.Value.MaxExtractedBytes} bytes (DocReader:Backup:MaxExtractedBytes).");
                }

                var destination = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await entry.ExtractToFileAsync(destination, overwrite: false, ct).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or EndOfStreamException)
        {
            throw Invalid("BACKUP_ARCHIVE_UNREADABLE", "The file is not a readable .tar.gz backup archive.");
        }

        return files;
    }

    private async Task<HashSet<string>> VerifyAsync(string root, HashSet<string> extracted, CancellationToken ct)
    {
        if (!extracted.Contains(BackupArchiveLayout.Checksums) || !extracted.Contains(BackupArchiveLayout.ChecksumsSignature))
        {
            throw Invalid("BACKUP_CHECKSUMS_MISSING", $"The archive has no {BackupArchiveLayout.Checksums} or no {BackupArchiveLayout.ChecksumsSignature}: it was not produced by a DocReader backup.");
        }

        var checksums = await File.ReadAllBytesAsync(Path.Combine(root, BackupArchiveLayout.Checksums), ct).ConfigureAwait(false);
        var signatureText = await File.ReadAllTextAsync(Path.Combine(root, BackupArchiveLayout.ChecksumsSignature), ct).ConfigureAwait(false);

        if (!signature.Verify(checksums, signatureText))
        {
            throw Invalid(
                "BACKUP_SIGNATURE_INVALID",
                "The signature of the checksum list does not match: the archive was altered, or made by an installation with a different STORAGE_CONFIG_ENCRYPTION_KEY.");
        }

        var listed = ChecksumList.Parse(checksums)
            ?? throw Invalid("BACKUP_CHECKSUM_MISMATCH", $"{BackupArchiveLayout.Checksums} is malformed.");

        var content = extracted
            .Where(path => path is not (BackupArchiveLayout.Checksums or BackupArchiveLayout.ChecksumsSignature))
            .ToHashSet(StringComparer.Ordinal);

        var missing = listed.Keys.Where(path => !content.Contains(path)).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw Invalid("BACKUP_CHECKSUM_MISMATCH", $"{missing.Count} file(s) listed in {BackupArchiveLayout.Checksums} are missing from the archive, starting with {missing[0]}.");
        }

        var unlisted = content.Where(path => !listed.ContainsKey(path)).Order(StringComparer.Ordinal).ToList();
        if (unlisted.Count > 0)
        {
            throw Invalid("BACKUP_CHECKSUM_MISMATCH", $"{unlisted.Count} file(s) in the archive are not in {BackupArchiveLayout.Checksums}, starting with {unlisted[0]}.");
        }

        foreach (var (path, expected) in listed.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var actual = await ChecksumList.HashFileAsync(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)), ct).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw Invalid("BACKUP_CHECKSUM_MISMATCH", $"The SHA-256 of {path} does not match {BackupArchiveLayout.Checksums}: the archive is corrupted.");
            }
        }

        if (!listed.ContainsKey(BackupArchiveLayout.DatabaseDump) || !listed.ContainsKey(BackupArchiveLayout.StorageManifest))
        {
            throw Invalid("BACKUP_MANIFEST_INVALID", $"The archive has no {BackupArchiveLayout.DatabaseDump} or no {BackupArchiveLayout.StorageManifest}.");
        }

        return [.. listed.Keys];
    }

    private static async Task<IReadOnlyList<BackupManifestEntry>> ReadManifestAsync(string root, HashSet<string> verified, CancellationToken ct)
    {
        StorageManifestFile? manifest;
        try
        {
            await using var stream = File.OpenRead(Path.Combine(root, BackupArchiveLayout.StorageManifest));
            manifest = await JsonSerializer.DeserializeAsync<StorageManifestFile>(stream, StorageManifestFile.SerializerOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw Invalid("BACKUP_MANIFEST_INVALID", $"{BackupArchiveLayout.StorageManifest} is not valid JSON.");
        }

        if (manifest?.Documents is null || manifest.FormatVersion != StorageManifestFile.CurrentFormatVersion)
        {
            throw Invalid("BACKUP_MANIFEST_INVALID", $"{BackupArchiveLayout.StorageManifest} is missing or has an unsupported format version.");
        }

        foreach (var entry in manifest.Documents)
        {
            if (entry.ArchivePath is not null &&
                (!string.Equals(entry.ArchivePath, BackupArchiveLayout.FilePathFor(entry.DocumentId), StringComparison.Ordinal) ||
                 !verified.Contains(entry.ArchivePath)))
            {
                throw Invalid("BACKUP_MANIFEST_INVALID", $"The manifest entry of document {entry.DocumentId} points at a file the archive does not hold.");
            }

            if (string.IsNullOrWhiteSpace(entry.StorageKey) || string.IsNullOrWhiteSpace(entry.MimeType))
            {
                throw Invalid("BACKUP_MANIFEST_INVALID", $"The manifest entry of document {entry.DocumentId} has no storage key or MIME type.");
            }
        }

        return [.. manifest.Documents.Select(entry => entry.ToEntry())];
    }

    private static BackupArchiveInvalidException Invalid(string errorCode, string message) => new(errorCode, message);
}
