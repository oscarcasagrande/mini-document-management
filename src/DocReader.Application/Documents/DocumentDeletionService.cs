using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using Microsoft.Extensions.Logging;

namespace DocReader.Application.Documents;

/// <summary>
/// Irreversible cleanup of RF-014: removes rows and the stored blob.
/// </summary>
public sealed class DocumentDeletionService(
    IDocumentRepository repository,
    IFileStorage storage,
    ILogger<DocumentDeletionService> logger)
{
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var document = await repository.FindByIdAsync(id, includeEvents: false, ct).ConfigureAwait(false);
        if (document is null)
        {
            throw new DocumentNotFoundException(id.ToString());
        }

        var storageKey = document.StorageKey;
        var repositoryId = document.StorageRepositoryId;

        // Rows go first: an orphan blob is recoverable by cleanup, an orphan row would keep showing a
        // document whose content can no longer be served.
        var removed = await repository.DeleteAsync(id, ct).ConfigureAwait(false);
        if (!removed)
        {
            throw new DocumentNotFoundException(id.ToString());
        }

        try
        {
            await storage.DeleteAsync(repositoryId, storageKey, ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Document rows were removed but the blob could not be deleted. documentId={DocumentId} storageKey={StorageKey}",
                id,
                storageKey);
            throw;
        }

        logger.LogInformation(
            "Document deleted. documentId={DocumentId} storageKey={StorageKey}",
            id,
            storageKey);
    }
}
