using System.Text.Json.Nodes;
using DocReader.Application.Abstractions;
using DocReader.Domain.Storage;

namespace DocReader.Application.Storage;

/// <summary>
/// The <see cref="IFileStorage"/> facade: finds the repository, decrypts its settings and hands the call to the adapter of
/// its provider. Adapters are cheap to build and hold no state worth keeping, so one is made per call and the settings
/// are read from the database each time: a change to a repository takes effect on the next request, on every process.
/// </summary>
public sealed class RepositoryFileStorage(
    IStorageRepositoryStore store,
    IStorageAdapterFactory adapters,
    ISecretProtector protector) : IFileStorage
{
    public async Task<StoredFile> SaveAsync(Guid repositoryId, Stream content, FileMetadata metadata, CancellationToken ct)
    {
        var adapter = await AdapterForAsync(repositoryId, ct).ConfigureAwait(false);

        return await adapter.SaveAsync(content, metadata, ct).ConfigureAwait(false);
    }

    public async Task<Stream> OpenReadAsync(Guid repositoryId, string storageKey, CancellationToken ct)
    {
        var adapter = await AdapterForAsync(repositoryId, ct).ConfigureAwait(false);

        return await adapter.OpenReadAsync(storageKey, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid repositoryId, string storageKey, CancellationToken ct)
    {
        var adapter = await AdapterForAsync(repositoryId, ct).ConfigureAwait(false);

        await adapter.DeleteAsync(storageKey, ct).ConfigureAwait(false);
    }

    public async Task<bool> IsWritableAsync(CancellationToken ct)
    {
        var repository = await store.FindDefaultAsync(ct).ConfigureAwait(false);
        if (repository is null)
        {
            return false;
        }

        var adapter = adapters.Create(repository.Provider, Decrypt(repository));

        return await adapter.IsWritableAsync(ct).ConfigureAwait(false);
    }

    private async Task<IStorageAdapter> AdapterForAsync(Guid repositoryId, CancellationToken ct)
    {
        var repository = await store.FindByIdAsync(repositoryId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Storage repository {repositoryId} does not exist.");

        return adapters.Create(repository.Provider, Decrypt(repository));
    }

    private JsonObject? Decrypt(StorageRepository repository) =>
        repository.EncryptedConnectionConfig is null
            ? null
            : JsonNode.Parse(protector.Unprotect(repository.EncryptedConnectionConfig)) as JsonObject;
}
