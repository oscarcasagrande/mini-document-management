namespace DocReader.Application.Abstractions;

/// <summary>
/// The storage of one repository, for one provider: a directory, a table, a bucket. It knows nothing about
/// which repository a document belongs to; <see cref="IFileStorage"/> picks the adapter.
/// </summary>
public interface IStorageAdapter
{
    Task<StoredFile> SaveAsync(Stream content, FileMetadata metadata, CancellationToken ct);

    /// <exception cref="FileNotFoundException">There is no such file.</exception>
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct);

    /// <summary>Removes the file. A file that is already gone is not an error.</summary>
    Task DeleteAsync(string storageKey, CancellationToken ct);

    /// <summary>Checks that the backing store is reachable and writable, for readiness probes.</summary>
    Task<bool> IsWritableAsync(CancellationToken ct);
}
