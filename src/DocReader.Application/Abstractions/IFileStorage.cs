namespace DocReader.Application.Abstractions;

/// <summary>
/// Storage contract of PRD section 11, as a facade over the configured repositories. A document is stored in the
/// repository chosen at upload and always read from, and deleted from, that same one: every call names it, and the
/// facade resolves the provider (file system, database, and in the future Azure Blob or S3) behind it.
/// </summary>
public interface IFileStorage
{
    Task<StoredFile> SaveAsync(Guid repositoryId, Stream content, FileMetadata metadata, CancellationToken ct);

    /// <exception cref="FileNotFoundException">The repository has no such file.</exception>
    Task<Stream> OpenReadAsync(Guid repositoryId, string storageKey, CancellationToken ct);

    Task DeleteAsync(Guid repositoryId, string storageKey, CancellationToken ct);

    /// <summary>Checks that the default repository is reachable and writable, for readiness probes.</summary>
    Task<bool> IsWritableAsync(CancellationToken ct);
}
