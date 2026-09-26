using DocReader.Application.Abstractions;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Application.Storage;
using DocReader.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocReader.Infrastructure.Persistence;

public sealed class StorageRepositoryStore(DocReaderDbContext dbContext, TimeProvider timeProvider) : IStorageRepositoryStore
{
    public Task<StorageRepository?> FindByIdAsync(Guid id, CancellationToken ct) =>
        dbContext.StorageRepositories.FirstOrDefaultAsync(repository => repository.Id == id, ct);

    public Task<StorageRepository?> FindByCodeAsync(string code, CancellationToken ct) =>
        dbContext.StorageRepositories.FirstOrDefaultAsync(repository => repository.Code == code, ct);

    public Task<StorageRepository?> FindDefaultAsync(CancellationToken ct) =>
        dbContext.StorageRepositories.FirstOrDefaultAsync(repository => repository.IsDefault, ct);

    public async Task<PagedResult<StorageRepository>> ListAsync(StorageRepositoryFilter filter, CancellationToken ct)
    {
        var query = dbContext.StorageRepositories.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.Code))
        {
            var pattern = Like.Contains(filter.Code);
            query = query.Where(repository => EF.Functions.ILike(repository.Code, pattern, Like.Escape));
        }

        if (filter.Provider is { } provider)
        {
            query = query.Where(repository => repository.Provider == provider);
        }

        if (filter.Active is { } active)
        {
            query = query.Where(repository => repository.Active == active);
        }

        var totalCount = await query.LongCountAsync(ct).ConfigureAwait(false);

        // The default first, so it is the first thing anyone sees.
        var items = await query
            .OrderByDescending(repository => repository.IsDefault)
            .ThenBy(repository => repository.Code)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<StorageRepository>(items, filter.Page, filter.PageSize, totalCount);
    }

    public async Task AddAsync(StorageRepository repository, bool makeDefault, CancellationToken ct)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async cancellationToken =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            if (makeDefault)
            {
                await UnsetCurrentDefaultAsync(exceptId: null, cancellationToken).ConfigureAwait(false);
                repository.SetDefault(true, timeProvider.GetUtcNow());
            }

            dbContext.StorageRepositories.Add(repository);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                dbContext.Entry(repository).State = EntityState.Detached;

                throw new ResourceConflictException(
                    "STORAGE_REPOSITORY_CODE_EXISTS",
                    $"A storage repository with the code {repository.Code} already exists.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task SetDefaultAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async cancellationToken =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            // Two saves, the old default first: the unique index on the default flag is checked per statement.
            await UnsetCurrentDefaultAsync(exceptId: id, cancellationToken).ConfigureAwait(false);

            var target = await dbContext.StorageRepositories.FirstAsync(repository => repository.Id == id, cancellationToken).ConfigureAwait(false);
            target.SetDefault(true, now);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public Task SaveChangesAsync(CancellationToken ct) => dbContext.SaveChangesAsync(ct);

    public async Task<bool> IsReferencedAsync(Guid id, CancellationToken ct) =>
        await HasDocumentsAsync(id, ct).ConfigureAwait(false)
        || await dbContext.ProductServices.AnyAsync(productService => productService.StorageRepositoryId == id, ct).ConfigureAwait(false);

    public Task<bool> HasDocumentsAsync(Guid id, CancellationToken ct) =>
        dbContext.Documents.AnyAsync(document => document.StorageRepositoryId == id, ct);

    public async Task RemoveAsync(StorageRepository repository, CancellationToken ct)
    {
        dbContext.StorageRepositories.Remove(repository);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task UnsetCurrentDefaultAsync(Guid? exceptId, CancellationToken ct)
    {
        var current = await dbContext.StorageRepositories
            .Where(repository => repository.IsDefault && repository.Id != exceptId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var repository in current)
        {
            repository.SetDefault(false, timeProvider.GetUtcNow());
        }

        if (current.Count > 0)
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
