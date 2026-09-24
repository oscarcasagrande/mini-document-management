namespace DocReader.Application.Abstractions;

/// <summary>
/// Storage contract of PRD section 11. The PoC binds it to a Docker volume; a future version may
/// bind it to S3, MinIO or Azure Blob without touching the domain or the public API.
/// </summary>
public interface IFileStorage
{
    Task<StoredFile> SaveAsync(Stream content, FileMetadata metadata, CancellationToken ct);

    Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct);

    Task DeleteAsync(string storageKey, CancellationToken ct);

    /// <summary>Checks that the backing store is reachable and writable, for readiness probes.</summary>
    Task<bool> IsWritableAsync(CancellationToken ct);
}
