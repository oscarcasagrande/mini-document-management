using DocReader.Application.Abstractions;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Application.Storage;
using DocReader.Domain.Storage;

namespace DocReader.UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="IStorageRepositoryStore"/>. It starts with the default file system repository the migration creates,
/// so anything that resolves a repository finds one.
/// </summary>
public sealed class InMemoryStorageRepositoryStore : IStorageRepositoryStore
{
    public InMemoryStorageRepositoryStore()
    {
        Items.Add(StorageRepository.Create(
            StorageRepository.DefaultRepositoryId, "DEFAULT", "Default (file system)", StorageProvider.FileSystem, null, true, true, DateTimeOffset.UnixEpoch));
    }

    public List<StorageRepository> Items { get; } = [];

    /// <summary>Repositories that documents are stored in, as the database would know from the documents.</summary>
    public HashSet<Guid> WithDocuments { get; } = [];

    /// <summary>Repositories a product names.</summary>
    public HashSet<Guid> UsedByProducts { get; } = [];

    public StorageRepository Default => Items.Single(repository => repository.IsDefault);

    public Task<StorageRepository?> FindByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(repository => repository.Id == id));

    public Task<StorageRepository?> FindByCodeAsync(string code, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(repository => repository.Code == code));

    public Task<StorageRepository?> FindDefaultAsync(CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(repository => repository.IsDefault));

    public Task<PagedResult<StorageRepository>> ListAsync(StorageRepositoryFilter filter, CancellationToken ct)
    {
        var query = Items.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter.Code))
        {
            query = query.Where(repository => repository.Code.Contains(filter.Code.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (filter.Provider is { } provider)
        {
            query = query.Where(repository => repository.Provider == provider);
        }

        if (filter.Active is { } active)
        {
            query = query.Where(repository => repository.Active == active);
        }

        var ordered = query.OrderByDescending(repository => repository.IsDefault).ThenBy(repository => repository.Code, StringComparer.Ordinal).ToList();

        return Task.FromResult(new PagedResult<StorageRepository>(
            [.. ordered.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)],
            filter.Page,
            filter.PageSize,
            ordered.Count));
    }

    public Task AddAsync(StorageRepository repository, bool makeDefault, CancellationToken ct)
    {
        if (Items.Any(item => item.Code == repository.Code))
        {
            throw new ResourceConflictException("STORAGE_REPOSITORY_CODE_EXISTS", "duplicate");
        }

        if (makeDefault)
        {
            foreach (var previous in Items.Where(item => item.IsDefault))
            {
                previous.SetDefault(false, DateTimeOffset.UnixEpoch);
            }

            repository.SetDefault(true, DateTimeOffset.UnixEpoch);
        }

        Items.Add(repository);

        return Task.CompletedTask;
    }

    public Task SetDefaultAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var repository in Items)
        {
            repository.SetDefault(repository.Id == id, now);
        }

        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<bool> IsReferencedAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(WithDocuments.Contains(id) || UsedByProducts.Contains(id));

    public Task<bool> HasDocumentsAsync(Guid id, CancellationToken ct) => Task.FromResult(WithDocuments.Contains(id));

    public Task RemoveAsync(StorageRepository repository, CancellationToken ct)
    {
        Items.Remove(repository);

        return Task.CompletedTask;
    }
}
