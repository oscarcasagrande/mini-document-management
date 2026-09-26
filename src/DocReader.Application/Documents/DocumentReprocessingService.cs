using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using DocReader.Application.Retention;
using Microsoft.Extensions.Logging;

namespace DocReader.Application.Documents;

/// <summary>
/// Reprocessing of RF-013: a new attempt that adds a new extraction and keeps the previous ones.
/// Concurrent processing of the same document is refused.
/// </summary>
public sealed class DocumentReprocessingService(
    IDocumentRepository repository,
    RetentionService retention,
    TimeProvider timeProvider,
    ILogger<DocumentReprocessingService> logger)
{
    /// <exception cref="DocumentNotFoundException">No document with this id.</exception>
    /// <exception cref="ReprocessConflictException">The document is queued or being processed.</exception>
    public async Task<Domain.Documents.Document> ReprocessAsync(Guid id, CancellationToken ct)
    {
        // The new cycle gets the policy that fits the document today: its detected type when it has one, and its
        // product. A policy edited since the upload is picked up here, which is the explicit way to apply it.
        var current = await repository.FindByIdAsync(id, includeEvents: false, ct).ConfigureAwait(false);
        var policy = current is null
            ? null
            : await retention
                .ResolveAsync(current.DetectedDocumentType ?? current.ExpectedDocumentType, current.ProductServiceId, ct)
                .ConfigureAwait(false);

        var outcome = await repository
            .QueueReprocessingAsync(id, timeProvider.GetUtcNow(), policy, ct)
            .ConfigureAwait(false);

        switch (outcome)
        {
            case ReprocessOutcome.NotFound:
                throw new DocumentNotFoundException(id.ToString());
            case ReprocessOutcome.Conflict:
                throw new ReprocessConflictException(id);
            case ReprocessOutcome.Purged:
                throw new DocumentPurgedException(id);
        }

        logger.LogInformation("Reprocessing requested. documentId={DocumentId}", id);

        var document = await repository.FindByIdAsync(id, includeEvents: false, ct).ConfigureAwait(false);
        return document ?? throw new DocumentNotFoundException(id.ToString());
    }
}
