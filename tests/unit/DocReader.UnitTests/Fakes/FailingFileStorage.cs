using DocReader.Application.Abstractions;

namespace DocReader.UnitTests.Fakes;

/// <summary>
/// Wraps a storage and makes every save into one repository throw, the way an unreachable or misconfigured cloud
/// repository does. Everything else goes to the inner storage.
/// </summary>
public sealed class FailingFileStorage(IFileStorage inner, Guid failingRepositoryId, Func<Exception> failure) : IFileStorage
{
    public int FailedSaves { get; private set; }

    public Task<StoredFile> SaveAsync(Guid repositoryId, Stream content, FileMetadata metadata, CancellationToken ct)
    {
        if (repositoryId == failingRepositoryId)
        {
            FailedSaves++;

            return Task.FromException<StoredFile>(failure());
        }

        return inner.SaveAsync(repositoryId, content, metadata, ct);
    }

    public Task<Stream> OpenReadAsync(Guid repositoryId, string storageKey, CancellationToken ct) =>
        inner.OpenReadAsync(repositoryId, storageKey, ct);

    public Task DeleteAsync(Guid repositoryId, string storageKey, CancellationToken ct) =>
        inner.DeleteAsync(repositoryId, storageKey, ct);

    public Task<bool> IsWritableAsync(CancellationToken ct) => inner.IsWritableAsync(ct);
}
