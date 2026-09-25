using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using Microsoft.Extensions.Logging;

namespace DocReader.Application.Documents;

/// <summary>
/// Reprocessing of RF-013: a new attempt that adds a new extraction and keeps the previous ones.
/// Concurrent processing of the same document is refused.
/// </summary>
public sealed class DocumentReprocessingService(
    IDocumentRepository repository,
    TimeProvider timeProvider,
    ILogger<DocumentReprocessingService> logger)
{
    /// <exception cref="DocumentNotFoundException">No document with this id.</exception>
    /// <exception cref="ReprocessConflictException">The document is queued or being processed.</exception>
    public async Task<Domain.Documents.Document> ReprocessAsync(Guid id, CancellationToken ct)
    {
        var outcome = await repository
            .QueueReprocessingAsync(id, timeProvider.GetUtcNow(), ct)
            .ConfigureAwait(false);

        switch (outcome)
        {
            case ReprocessOutcome.NotFound:
                throw new DocumentNotFoundException(id.ToString());
            case ReprocessOutcome.Conflict:
                throw new ReprocessConflictException(id);
        }

        logger.LogInformation("Reprocessing requested. documentId={DocumentId}", id);

        var document = await repository.FindByIdAsync(id, includeEvents: false, ct).ConfigureAwait(false);
        return document ?? throw new DocumentNotFoundException(id.ToString());
    }
}
