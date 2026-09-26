using DocReader.Application.Abstractions;
using DocReader.Application.Catalog;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocReader.Infrastructure.Persistence;

public sealed class ProductServiceRepository(DocReaderDbContext dbContext) : IProductServiceRepository
{
    public Task<ProductService?> FindByIdAsync(Guid id, CancellationToken ct) =>
        dbContext.ProductServices.Include(productService => productService.StorageRepository)
            .FirstOrDefaultAsync(productService => productService.Id == id, ct);

    public Task<ProductService?> FindByCodeAsync(string code, CancellationToken ct) =>
        dbContext.ProductServices.FirstOrDefaultAsync(productService => productService.Code == code, ct);

    public async Task<PagedResult<ProductService>> ListAsync(ProductServiceFilter filter, CancellationToken ct)
    {
        var query = dbContext.ProductServices.AsNoTracking().Include(productService => productService.StorageRepository).AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Code))
        {
            var pattern = Like.Contains(filter.Code);
            query = query.Where(productService => EF.Functions.ILike(productService.Code, pattern, Like.Escape));
        }

        if (!string.IsNullOrWhiteSpace(filter.Name))
        {
            var pattern = Like.Contains(filter.Name);
            query = query.Where(productService => EF.Functions.ILike(productService.Name, pattern, Like.Escape));
        }

        if (filter.Active is { } active)
        {
            query = query.Where(productService => productService.Active == active);
        }

        var totalCount = await query.LongCountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderBy(productService => productService.Code)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<ProductService>(items, filter.Page, filter.PageSize, totalCount);
    }

    public async Task AddAsync(ProductService productService, CancellationToken ct)
    {
        dbContext.ProductServices.Add(productService);

        try
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.Entry(productService).State = EntityState.Detached;

            throw new ResourceConflictException(
                "PRODUCT_SERVICE_CODE_EXISTS",
                $"A product or service with the code {productService.Code} already exists.");
        }
    }

    public Task SaveChangesAsync(CancellationToken ct) => dbContext.SaveChangesAsync(ct);

    public async Task<bool> IsReferencedAsync(Guid id, CancellationToken ct) =>
        await dbContext.Documents.AnyAsync(document => document.ProductServiceId == id, ct).ConfigureAwait(false)
        || await dbContext.RetentionPolicies.AnyAsync(policy => policy.ProductServiceId == id, ct).ConfigureAwait(false)
        || await dbContext.WebhookSubscriptions.AnyAsync(subscription => subscription.ProductServiceId == id, ct).ConfigureAwait(false);

    public async Task RemoveAsync(ProductService productService, CancellationToken ct)
    {
        dbContext.ProductServices.Remove(productService);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
