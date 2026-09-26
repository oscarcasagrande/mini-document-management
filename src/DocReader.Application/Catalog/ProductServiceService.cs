using DocReader.Application.Abstractions;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Domain.Catalog;
using Microsoft.Extensions.Logging;

namespace DocReader.Application.Catalog;

/// <summary>Registry of the products and services documents can be linked to.</summary>
public sealed class ProductServiceService(
    IProductServiceRepository repository,
    IStorageRepositoryStore storageRepositories,
    TimeProvider timeProvider,
    ILogger<ProductServiceService> logger)
{
    public Task<PagedResult<ProductService>> ListAsync(ProductServiceFilter filter, CancellationToken ct) =>
        repository.ListAsync(filter, ct);

    public async Task<ProductService> GetAsync(Guid id, CancellationToken ct) =>
        await repository.FindByIdAsync(id, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("product-service", id.ToString());

    public async Task<ProductService> CreateAsync(
        string? code,
        string? name,
        bool active,
        Guid? storageRepositoryId,
        CancellationToken ct)
    {
        var normalized = ProductService.NormalizeCode(code)
            ?? throw new RequestValidationException(
                "INVALID_PRODUCT_SERVICE_CODE",
                $"The code must have 1 to {ProductService.MaxCodeLength} letters, digits, dots, hyphens or underscores, starting with a letter or digit.");
        var cleanName = ValidateName(name);
        await EnsureStorageRepositoryAsync(storageRepositoryId, ct).ConfigureAwait(false);

        if (await repository.FindByCodeAsync(normalized, ct).ConfigureAwait(false) is not null)
        {
            throw new ResourceConflictException(
                "PRODUCT_SERVICE_CODE_EXISTS",
                $"A product or service with the code {normalized} already exists.");
        }

        var now = timeProvider.GetUtcNow();
        var productService = ProductService.Create(Guid.CreateVersion7(now), normalized, cleanName, active, now);
        productService.UseStorageRepository(storageRepositoryId, now);

        await repository.AddAsync(productService, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Product service created. productServiceId={ProductServiceId} code={Code}",
            productService.Id,
            productService.Code);

        return productService;
    }

    public async Task<ProductService> UpdateAsync(
        Guid id,
        string? name,
        bool active,
        Guid? storageRepositoryId,
        CancellationToken ct)
    {
        var productService = await GetAsync(id, ct).ConfigureAwait(false);
        var cleanName = ValidateName(name);
        await EnsureStorageRepositoryAsync(storageRepositoryId, ct).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        productService.Update(cleanName, active, now);
        productService.UseStorageRepository(storageRepositoryId, now);
        await repository.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Product service updated. productServiceId={ProductServiceId} active={Active}",
            productService.Id,
            productService.Active);

        return productService;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var productService = await GetAsync(id, ct).ConfigureAwait(false);

        if (await repository.IsReferencedAsync(id, ct).ConfigureAwait(false))
        {
            throw new ResourceConflictException(
                "PRODUCT_SERVICE_IN_USE",
                "Documents, retention policies or webhook subscriptions still refer to this product or service. Deactivate it (active = false) instead of deleting it.");
        }

        await repository.RemoveAsync(productService, ct).ConfigureAwait(false);

        logger.LogInformation("Product service deleted. productServiceId={ProductServiceId}", id);
    }

    /// <summary>A product can only name a repository that exists and takes documents.</summary>
    private async Task EnsureStorageRepositoryAsync(Guid? storageRepositoryId, CancellationToken ct)
    {
        if (storageRepositoryId is not { } id)
        {
            return;
        }

        var storageRepository = await storageRepositories.FindByIdAsync(id, ct).ConfigureAwait(false)
            ?? throw new UnprocessableRequestException(
                "STORAGE_REPOSITORY_NOT_FOUND",
                $"There is no storage repository with the id {id}.");

        if (!storageRepository.Active)
        {
            throw new UnprocessableRequestException(
                "STORAGE_REPOSITORY_INACTIVE",
                $"The storage repository {storageRepository.Code} is inactive and takes no new documents.");
        }
    }

    private static string ValidateName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new RequestValidationException("INVALID_PRODUCT_SERVICE_NAME", "The name is required.");
        }

        return trimmed.Length > ProductService.MaxNameLength
            ? throw new RequestValidationException(
                "INVALID_PRODUCT_SERVICE_NAME",
                $"The name must not exceed {ProductService.MaxNameLength} characters.")
            : trimmed;
    }
}
