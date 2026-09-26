using DocReader.Application.Documents;
using DocReader.Application.Storage;
using DocReader.Domain.Storage;

namespace DocReader.Application.Abstractions;

/// <summary>Persistence of the storage repositories. Finds by id return tracked entities, so a change is saved with <see cref="SaveChangesAsync"/>.</summary>
public interface IStorageRepositoryStore
{
    Task<StorageRepository?> FindByIdAsync(Guid id, CancellationToken ct);

    Task<StorageRepository?> FindByCodeAsync(string code, CancellationToken ct);

    /// <summary>The repository flagged as default. There is always exactly one.</summary>
    Task<StorageRepository?> FindDefaultAsync(CancellationToken ct);

    Task<PagedResult<StorageRepository>> ListAsync(StorageRepositoryFilter filter, CancellationToken ct);

    /// <summary>Adds the repository; with <paramref name="makeDefault"/> the previous default stops being one, in the same transaction.</summary>
    /// <exception cref="Errors.ResourceConflictException">The code is already taken.</exception>
    Task AddAsync(StorageRepository repository, bool makeDefault, CancellationToken ct);

    /// <summary>Makes the repository the default and the previous default a regular one, in one transaction.</summary>
    Task SetDefaultAsync(Guid id, DateTimeOffset now, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>Whether a document is stored in it, or a product or service names it.</summary>
    Task<bool> IsReferencedAsync(Guid id, CancellationToken ct);

    /// <summary>Whether a document is stored in it (a product naming it does not count: it holds no file).</summary>
    Task<bool> HasDocumentsAsync(Guid id, CancellationToken ct);

    Task RemoveAsync(StorageRepository repository, CancellationToken ct);
}
