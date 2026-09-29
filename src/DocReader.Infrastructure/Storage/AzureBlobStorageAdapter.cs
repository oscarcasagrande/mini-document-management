using System.Text.Json.Nodes;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DocReader.Application.Abstractions;

namespace DocReader.Infrastructure.Storage;

/// <summary>
/// The <see cref="Domain.Storage.StorageProvider.AzureBlobStorage"/> adapter: stores originals as blobs in the
/// container named by the repository's <c>container</c> setting, using the same
/// <c>documents/yyyy/MM/dd/{id}/original{ext}</c> key layout as the file system adapter (<see cref="StorageKeyLayout"/>).
/// One client per adapter instance; the factory builds a fresh adapter per call, so nothing here is cached across
/// requests.
/// </summary>
public sealed class AzureBlobStorageAdapter : IStorageAdapter
{
    private const string ReadinessProbeBlobName = ".readiness-probe";

    private readonly BlobContainerClient _container;

    /// <param name="connectionConfig">Decrypted settings of the repository: <c>connectionString</c> and <c>container</c>, both required (validated by <see cref="Application.Storage.StorageConnectionConfig"/> before a repository can be saved).</param>
    public AzureBlobStorageAdapter(JsonObject? connectionConfig)
    {
        var connectionString = RequireString(connectionConfig, "connectionString");
        var containerName = RequireString(connectionConfig, "container");
        _container = new BlobContainerClient(connectionString, containerName);
    }

    public async Task<StoredFile> SaveAsync(Stream content, FileMetadata metadata, CancellationToken ct)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);

        var storageKey = StorageKeyLayout.BuildKey(metadata);
        var blob = _container.GetBlobClient(storageKey);

        var headers = new BlobHttpHeaders { ContentType = metadata.MimeType };
        await blob.UploadAsync(content, new BlobUploadOptions { HttpHeaders = headers }, ct).ConfigureAwait(false);

        var properties = await blob.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);

        return new StoredFile(storageKey, properties.Value.ContentLength);
    }

    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(storageKey);

        try
        {
            var download = await blob.DownloadStreamingAsync(cancellationToken: ct).ConfigureAwait(false);
            return download.Value.Content;
        }
        catch (RequestFailedException exception) when (exception.ErrorCode == BlobErrorCode.BlobNotFound || exception.Status == 404)
        {
            throw new FileNotFoundException("Stored original is not available.", storageKey);
        }
    }

    public async Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(storageKey);
        await blob.DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<bool> IsWritableAsync(CancellationToken ct)
    {
        try
        {
            await _container.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);

            var probe = _container.GetBlobClient(ReadinessProbeBlobName);
            using var content = new MemoryStream("ok"u8.ToArray());
            await probe.UploadAsync(content, overwrite: true, ct).ConfigureAwait(false);
            await probe.DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static string RequireString(JsonObject? config, string key) =>
        config?[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : throw new InvalidOperationException(
                $"The Azure Blob Storage repository is missing the '{key}' setting, or it was not decrypted.");
}
