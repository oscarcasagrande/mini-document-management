using System.Globalization;
using System.Text.RegularExpressions;
using DocReader.Application.Abstractions;
using DocReader.Domain.Storage;
using Microsoft.Extensions.Logging;

namespace DocReader.Infrastructure.Storage;

/// <summary>
/// The <see cref="StorageProvider.FileSystem"/> adapter: stores originals in a directory of the local filesystem, which is
/// a Docker volume in the PoC. Keys are derived from the document UUID and the detected extension, never from the name
/// sent by the client, and every read resolves strictly under the root of the adapter (PRD section 21).
/// </summary>
public sealed partial class FileSystemStorageAdapter : IStorageAdapter
{
    private const string OriginalFileName = "original";

    private readonly string _root;
    private readonly ILogger<FileSystemStorageAdapter> _logger;

    /// <param name="rootPath">The directory the adapter stores under. Created if it does not exist.</param>
    /// <param name="logger">Logger.</param>
    public FileSystemStorageAdapter(string rootPath, ILogger<FileSystemStorageAdapter> logger)
    {
        _logger = logger;
        _root = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredFile> SaveAsync(Stream content, FileMetadata metadata, CancellationToken ct)
    {
        var storageKey = BuildStorageKey(metadata);
        var absolutePath = ResolveAbsolutePath(storageKey);

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        // Write to a temporary neighbour and move into place, so a crash never leaves a half written
        // original behind a document row that claims it exists.
        var temporaryPath = absolutePath + ".part";

        try
        {
            long written;
            await using (var destination = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous
                }))
            {
                await content.CopyToAsync(destination, ct).ConfigureAwait(false);
                await destination.FlushAsync(ct).ConfigureAwait(false);
                written = destination.Length;
            }

            File.Move(temporaryPath, absolutePath, overwrite: true);

            _logger.LogDebug(
                "Stored original. storageKey={StorageKey} sizeBytes={SizeBytes}",
                storageKey,
                written);

            return new StoredFile(storageKey, written);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct)
    {
        var absolutePath = ResolveAbsolutePath(storageKey);

        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("Stored original is not available.", storageKey);
        }

        Stream stream = new FileStream(
            absolutePath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous
            });

        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        var absolutePath = ResolveAbsolutePath(storageKey);

        TryDelete(absolutePath);
        TryRemoveEmptyDirectories(Path.GetDirectoryName(absolutePath));

        return Task.CompletedTask;
    }

    public async Task<bool> IsWritableAsync(CancellationToken ct)
    {
        var probePath = Path.Combine(_root, ".readiness-probe");

        try
        {
            await File.WriteAllTextAsync(probePath, "ok", ct).ConfigureAwait(false);
            File.Delete(probePath);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Storage readiness probe failed. root={Root}", _root);
            return false;
        }
    }

    /// <summary>
    /// Builds a key of the form <c>documents/yyyy/MM/dd/{documentId}/original{ext}</c>. The date
    /// prefix only keeps directories small; identity comes from the UUID.
    /// </summary>
    private static string BuildStorageKey(FileMetadata metadata)
    {
        var extension = NormalizeExtension(metadata.Extension);
        var day = metadata.CreatedAt.UtcDateTime;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"documents/{day:yyyy}/{day:MM}/{day:dd}/{metadata.DocumentId:D}/{OriginalFileName}{extension}");
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var candidate = extension.StartsWith('.') ? extension : "." + extension;

        return SafeExtensionPattern().IsMatch(candidate)
            ? candidate.ToLowerInvariant()
            : throw new ArgumentException($"Extension {extension} is not a safe storage extension.", nameof(extension));
    }

    /// <summary>
    /// Maps a logical key to an absolute path and refuses anything that could escape the root.
    /// </summary>
    private string ResolveAbsolutePath(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey) ||
            storageKey.Contains("..", StringComparison.Ordinal) ||
            !SafeStorageKeyPattern().IsMatch(storageKey))
        {
            throw new ArgumentException("Storage key has an unexpected shape.", nameof(storageKey));
        }

        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        var combined = Path.GetFullPath(Path.Combine(_root, storageKey.Replace('/', Path.DirectorySeparatorChar)));

        if (!combined.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException("Storage key resolves outside of the storage root.", nameof(storageKey));
        }

        return combined;
    }

    private static void TryDelete(string absolutePath)
    {
        try
        {
            File.Delete(absolutePath);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing to remove.
        }
        catch (FileNotFoundException)
        {
            // Nothing to remove.
        }
    }

    private void TryRemoveEmptyDirectories(string? directory)
    {
        while (!string.IsNullOrEmpty(directory) &&
               directory.StartsWith(_root, StringComparison.Ordinal) &&
               !string.Equals(directory, _root, StringComparison.Ordinal))
        {
            try
            {
                if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    return;
                }

                Directory.Delete(directory);
            }
            catch (IOException)
            {
                return;
            }

            directory = Path.GetDirectoryName(directory);
        }
    }

    /// <summary>Only lowercase alphanumeric extensions of up to four characters are accepted.</summary>
    [GeneratedRegex(@"^\.[a-z0-9]{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeExtensionPattern();

    /// <summary>
    /// Keys are built by this class alone: forward slashes, no dot segments, no backslashes.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,500}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeStorageKeyPattern();
}
