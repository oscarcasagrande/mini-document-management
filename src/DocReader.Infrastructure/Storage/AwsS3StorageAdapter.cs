using DocReader.Application.Abstractions;

namespace DocReader.Infrastructure.Storage;

/// <summary>
/// The <see cref="Domain.Storage.StorageProvider.AwsS3"/> adapter. The provider is registered, so a repository can be
/// created for it and its settings are validated, but the adapter itself is not implemented: every call throws.
///
/// TODO(aws-s3): implement with the AWSSDK.S3 package. The settings are already validated and stored: <c>bucket</c>,
/// <c>accessKeyId</c>, <c>secretAccessKey</c> and, optionally, <c>region</c> and <c>serviceUrl</c> (which also makes MinIO
/// and other S3-compatible services work). Save: <c>PutObject</c> with the storage key as the object key (same layout as
/// the file system adapter). Open: <c>GetObject</c>, mapping NoSuchKey to <see cref="FileNotFoundException"/>. Delete:
/// <c>DeleteObject</c>. IsWritable: put and delete a probe object. Then remove <c>AwsS3</c> from the "not implemented" list
/// in <c>StorageRepository.IsProviderImplemented</c>.
/// </summary>
public sealed class AwsS3StorageAdapter : IStorageAdapter
{
    private const string NotImplemented =
        "The AWS S3 adapter is not implemented yet. Store documents in a FileSystem or Database repository.";

    public Task<StoredFile> SaveAsync(Stream content, FileMetadata metadata, CancellationToken ct) =>
        throw new NotImplementedException(NotImplemented);

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct) =>
        throw new NotImplementedException(NotImplemented);

    public Task DeleteAsync(string storageKey, CancellationToken ct) =>
        throw new NotImplementedException(NotImplemented);

    public Task<bool> IsWritableAsync(CancellationToken ct) =>
        throw new NotImplementedException(NotImplemented);
}
