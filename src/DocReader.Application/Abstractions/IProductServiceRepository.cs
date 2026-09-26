using DocReader.Application.Catalog;
using DocReader.Application.Documents;
using DocReader.Domain.Catalog;

namespace DocReader.Application.Abstractions;

/// <summary>Persistence of the products and services. Finds return tracked entities, so a change is saved with <see cref="SaveChangesAsync"/>.</summary>
public interface IProductServiceRepository
{
    Task<ProductService?> FindByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Looks a product up by its canonical (upper case) code.</summary>
    Task<ProductService?> FindByCodeAsync(string code, CancellationToken ct);

    Task<PagedResult<ProductService>> ListAsync(ProductServiceFilter filter, CancellationToken ct);

    /// <exception cref="Errors.ResourceConflictException">The code is already taken.</exception>
    Task AddAsync(ProductService productService, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>Whether a document, a retention policy or a webhook still points at the product.</summary>
    Task<bool> IsReferencedAsync(Guid id, CancellationToken ct);

    Task RemoveAsync(ProductService productService, CancellationToken ct);
}
