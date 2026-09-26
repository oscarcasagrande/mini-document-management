using System.Text.Json.Nodes;
using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using DocReader.Application.Storage;
using DocReader.Domain.Storage;
using DocReader.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocReader.Infrastructure.Storage;

/// <summary>
/// Builds the adapter of a provider. The file system adapter is rooted at the configured storage root, plus the
/// repository's optional <c>directory</c> setting, which is validated as a relative path and checked again here so that
/// no configuration can place it outside the root.
/// </summary>
public sealed class StorageAdapterFactory(
    DocReaderDbContext dbContext,
    IOptions<StorageOptions> options,
    ILoggerFactory loggerFactory) : IStorageAdapterFactory
{
    public IStorageAdapter Create(StorageProvider provider, JsonObject? connectionConfig) => provider switch
    {
        StorageProvider.FileSystem => new FileSystemStorageAdapter(
            ResolveRoot(StorageConnectionConfig.DirectoryOf(connectionConfig)),
            loggerFactory.CreateLogger<FileSystemStorageAdapter>()),
        StorageProvider.Database => new DatabaseStorageAdapter(dbContext),
        StorageProvider.AzureBlobStorage => new AzureBlobStorageAdapter(),
        StorageProvider.AwsS3 => new AwsS3StorageAdapter(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown storage provider.")
    };

    private string ResolveRoot(string? directory)
    {
        var root = Path.GetFullPath(options.Value.RootPath);
        if (directory is null)
        {
            return root;
        }

        if (!StorageConnectionConfig.IsSafeDirectory(directory))
        {
            throw new InvalidOperationException("The directory setting of the repository is not a safe relative path.");
        }

        var combined = Path.GetFullPath(Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        return combined.StartsWith(rootWithSeparator, StringComparison.Ordinal)
            ? combined
            : throw new InvalidOperationException("The directory setting of the repository resolves outside the storage root.");
    }
}
