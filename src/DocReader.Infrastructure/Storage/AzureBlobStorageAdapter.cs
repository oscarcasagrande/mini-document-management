using DocReader.Application.Abstractions;

namespace DocReader.Infrastructure.Storage;

/// <summary>
/// The <see cref="Domain.Storage.StorageProvider.AzureBlobStorage"/> adapter. The provider is registered, so a repository
/// can be created for it and its settings are validated, but the adapter itself is not implemented: every call throws.
///
/// TODO(azure-blob): implement with the Azure.Storage.Blobs package. The settings are already validated and stored:
/// <c>connectionString</c> and <c>container</c>. Save: upload the stream as the blob named by the storage key (same
/// <c>documents/yyyy/MM/dd/{id}/original{ext}</c> layout as the file system adapter). Open: download to a stream, and map
/// BlobNotFound to <see cref="FileNotFoundException"/>. Delete: <c>DeleteIfExistsAsync</c>. IsWritable: create and delete a
/// probe blob. Then remove <c>Azure</c> from the "not implemented" list in <c>StorageRepository.IsProviderImplemented</c>.
/// </summary>
public sealed class AzureBlobStorageAdapter : IStorageAdapter
{
    private const string NotImplemented =
        "The Azure Blob Storage adapter is not implemented yet. Store documents in a FileSystem or Database repository.";

    public Task<StoredFile> SaveAsync(Stream content, FileMetadata metadata, CancellationToken ct) =>
        throw new NotImplementedException(NotImplemented);

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct) =>
        throw new NotImplementedException(NotImplemented);

    public Task DeleteAsync(string storageKey, CancellationToken ct) =>
        throw new NotImplementedException(NotImplemented);

    public Task<bool> IsWritableAsync(CancellationToken ct) =>
        throw new NotImplementedException(NotImplemented);
}
