using DocReader.Application.Abstractions;
using DocReader.Application.Catalog;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Domain.Catalog;

namespace DocReader.UnitTests.Fakes;

/// <summary>In-memory <see cref="IProductServiceRepository"/> for the use case tests.</summary>
public sealed class InMemoryProductServiceStore : IProductServiceRepository
{
    public List<ProductService> Items { get; } = [];

    /// <summary>Stands in for the documents, policies and webhooks that point at a product.</summary>
    public Func<Guid, bool> IsReferenced { get; set; } = _ => false;

    public Task<ProductService?> FindByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(item => item.Id == id));

    public Task<ProductService?> FindByCodeAsync(string code, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(item => item.Code == code));

    public Task<PagedResult<ProductService>> ListAsync(ProductServiceFilter filter, CancellationToken ct)
    {
        var query = Items.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter.Code))
        {
            query = query.Where(item => item.Code.Contains(filter.Code.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.Name))
        {
            query = query.Where(item => item.Name.Contains(filter.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (filter.Active is { } active)
        {
            query = query.Where(item => item.Active == active);
        }

        var ordered = query.OrderBy(item => item.Code, StringComparer.Ordinal).ToList();
        var page = ordered.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToList();

        return Task.FromResult(new PagedResult<ProductService>(page, filter.Page, filter.PageSize, ordered.Count));
    }

    public Task AddAsync(ProductService productService, CancellationToken ct)
    {
        if (Items.Any(item => item.Code == productService.Code))
        {
            throw new ResourceConflictException("PRODUCT_SERVICE_CODE_EXISTS", "duplicate");
        }

        Items.Add(productService);

        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<bool> IsReferencedAsync(Guid id, CancellationToken ct) => Task.FromResult(IsReferenced(id));

    public Task RemoveAsync(ProductService productService, CancellationToken ct)
    {
        Items.Remove(productService);

        return Task.CompletedTask;
    }
}
