using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using DocReader.Domain.Documents;
using Microsoft.Extensions.Logging;

namespace DocReader.Application.Documents;

/// <summary>
/// Read side of the API: listing, detail, status and original content. All of it works without
/// login (RF-005, RF-006) and stays available even when processing failed.
/// </summary>
public sealed class DocumentQueryService(
    IDocumentRepository repository,
    IFileStorage storage,
    ILogger<DocumentQueryService> logger)
{
    public Task<PagedResult<Document>> ListAsync(DocumentListFilter filter, CancellationToken ct) =>
        repository.ListAsync(filter, ct);

    public async Task<Document> GetByIdAsync(Guid id, bool includeEvents, CancellationToken ct)
    {
        var document = await repository.FindByIdAsync(id, includeEvents, ct).ConfigureAwait(false);
        return document ?? throw new DocumentNotFoundException(id.ToString());
    }

    public async Task<Document> GetByProtocolAsync(string protocol, bool includeEvents, CancellationToken ct)
    {
        if (!DocumentProtocol.IsWellFormed(protocol))
        {
            throw new DocumentNotFoundException(protocol);
        }

        var document = await repository.FindByProtocolAsync(protocol, includeEvents, ct).ConfigureAwait(false);
        return document ?? throw new DocumentNotFoundException(protocol);
    }

    /// <summary>
    /// Opens the original bytes. The metadata is authoritative for the content type, so a renamed
    /// or mislabeled upload is still served as what it really is.
    /// </summary>
    public async Task<DocumentContentResult> OpenContentAsync(Guid id, CancellationToken ct)
    {
        var document = await GetByIdAsync(id, includeEvents: false, ct).ConfigureAwait(false);

        Stream content;
        try
        {
            content = await storage.OpenReadAsync(document.StorageKey, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            logger.LogError(
                "Stored blob is missing for an existing document. documentId={DocumentId} storageKey={StorageKey}",
                document.Id,
                document.StorageKey);
            throw new DocumentContentMissingException(document.Id);
        }

        return new DocumentContentResult(
            content,
            document.MimeType,
            document.OriginalFileName,
            document.SizeBytes,
            document.Sha256);
    }
}
