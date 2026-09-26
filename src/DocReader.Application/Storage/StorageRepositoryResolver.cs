using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using DocReader.Domain.Catalog;
using DocReader.Domain.Storage;

namespace DocReader.Application.Storage;

/// <summary>
/// Chooses the repository a new document is stored in: the one its product or service names, or the default. The
/// choice is recorded on the document, so it is made once, at upload.
/// </summary>
public sealed class StorageRepositoryResolver(IStorageRepositoryStore store)
{
    public async Task<StorageRepository> ResolveForUploadAsync(ProductService? productService, CancellationToken ct)
    {
        var repository = productService?.StorageRepositoryId is { } id
            ? await store.FindByIdAsync(id, ct).ConfigureAwait(false)
            : await store.FindDefaultAsync(ct).ConfigureAwait(false);

        if (repository is null)
        {
            throw new InvalidOperationException(
                productService?.StorageRepositoryId is null
                    ? "There is no default storage repository. The migration creates one; it must not be missing."
                    : $"The storage repository of product {productService.Code} does not exist.");
        }

        return repository.Active
            ? repository
            : throw new UploadRejectedException(
                UploadRejectionReason.UnprocessableContent,
                "STORAGE_REPOSITORY_INACTIVE",
                $"The storage repository {repository.Code} is inactive and takes no new documents.");
    }
}
