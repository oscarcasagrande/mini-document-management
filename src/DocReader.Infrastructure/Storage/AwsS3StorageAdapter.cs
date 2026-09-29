using System.Net;
using System.Text.Json.Nodes;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using DocReader.Application.Abstractions;

namespace DocReader.Infrastructure.Storage;

/// <summary>
/// The <see cref="Domain.Storage.StorageProvider.AwsS3"/> adapter: stores originals as objects in the bucket named
/// by the repository's <c>bucket</c> setting, using the same <c>documents/yyyy/MM/dd/{id}/original{ext}</c> key
/// layout as the file system adapter (<see cref="StorageKeyLayout"/>). The optional <c>serviceUrl</c> setting also
/// makes MinIO and other S3-compatible services work, with a path-style endpoint. One client per adapter instance;
/// the factory builds a fresh adapter per call, so nothing here is cached across requests.
/// </summary>
public sealed class AwsS3StorageAdapter : IStorageAdapter
{
    private const string ReadinessProbeKey = ".readiness-probe";

    private readonly IAmazonS3 _client;
    private readonly string _bucket;

    /// <param name="connectionConfig">
    /// Decrypted settings of the repository: <c>bucket</c>, <c>accessKeyId</c> and <c>secretAccessKey</c> are
    /// required; <c>region</c> and <c>serviceUrl</c> are optional (validated by
    /// <see cref="Application.Storage.StorageConnectionConfig"/> before a repository can be saved).
    /// </param>
    public AwsS3StorageAdapter(JsonObject? connectionConfig)
    {
        _bucket = RequireString(connectionConfig, "bucket");
        var accessKeyId = RequireString(connectionConfig, "accessKeyId");
        var secretAccessKey = RequireString(connectionConfig, "secretAccessKey");

        var config = new AmazonS3Config();

        if (OptionalString(connectionConfig, "region") is { } region)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }

        if (OptionalString(connectionConfig, "serviceUrl") is { } serviceUrl)
        {
            // MinIO and most other S3-compatible services need the bucket in the path, not as a subdomain.
            config.ServiceURL = serviceUrl;
            config.ForcePathStyle = true;
        }

        _client = new AmazonS3Client(accessKeyId, secretAccessKey, config);
    }

    public async Task<StoredFile> SaveAsync(Stream content, FileMetadata metadata, CancellationToken ct)
    {
        var storageKey = StorageKeyLayout.BuildKey(metadata);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = storageKey,
            InputStream = buffer,
            ContentType = metadata.MimeType,
            AutoCloseStream = false
        };

        await _client.PutObjectAsync(request, ct).ConfigureAwait(false);

        return new StoredFile(storageKey, buffer.Length);
    }

    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct)
    {
        try
        {
            // The caller owns the returned stream and disposes it; disposing the response here would close it too.
            var response = await _client.GetObjectAsync(_bucket, storageKey, ct).ConfigureAwait(false);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode == HttpStatusCode.NotFound
            || string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.Ordinal))
        {
            throw new FileNotFoundException("Stored original is not available.", storageKey);
        }
    }

    public async Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        // DeleteObject is idempotent on S3 itself: deleting a key that is already gone is not an error.
        await _client.DeleteObjectAsync(_bucket, storageKey, ct).ConfigureAwait(false);
    }

    public async Task<bool> IsWritableAsync(CancellationToken ct)
    {
        try
        {
            using var content = new MemoryStream("ok"u8.ToArray());
            await _client.PutObjectAsync(
                new PutObjectRequest { BucketName = _bucket, Key = ReadinessProbeKey, InputStream = content, AutoCloseStream = false },
                ct).ConfigureAwait(false);
            await _client.DeleteObjectAsync(_bucket, ReadinessProbeKey, ct).ConfigureAwait(false);

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
                $"The AWS S3 repository is missing the '{key}' setting, or it was not decrypted.");

    private static string? OptionalString(JsonObject? config, string key) =>
        config?[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
