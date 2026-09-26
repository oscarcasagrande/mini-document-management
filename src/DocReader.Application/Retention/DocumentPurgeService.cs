using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocReader.Application.Retention;

/// <summary>What one purge run did.</summary>
/// <param name="Purged">Documents whose file was removed and that are now PURGED.</param>
/// <param name="Failed">Documents that could not be purged this time; the next run tries them again.</param>
public sealed record PurgeResult(int Purged, int Failed);

/// <summary>
/// Removes the original file of every document whose retention period ended, and marks it PURGED. Safe to run at
/// any time and any number of times, including from two workers at once: removing a file that is already gone is
/// not an error, and only a document that is still eligible when it is marked changes status.
/// </summary>
public sealed class DocumentPurgeService(
    IDocumentRepository repository,
    IFileStorage storage,
    IOptions<PurgeOptions> options,
    TimeProvider timeProvider,
    ILogger<DocumentPurgeService> logger)
{
    public async Task<PurgeResult> PurgeExpiredAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var batchSize = options.Value.BatchSize;
        var purged = 0;
        var failedIds = new HashSet<Guid>();

        while (!ct.IsCancellationRequested)
        {
            var batch = await repository.FindPurgeableAsync(now, batchSize, failedIds, ct).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var candidate in batch)
            {
                try
                {
                    await storage.DeleteAsync(candidate.StorageRepositoryId, candidate.StorageKey, ct).ConfigureAwait(false);

                    if (await repository.MarkPurgedAsync(candidate.DocumentId, now, ct).ConfigureAwait(false))
                    {
                        purged++;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failedIds.Add(candidate.DocumentId);

                    // Identifiers and the error type only: never the file name or anything read from the document.
                    logger.LogError(
                        exception,
                        "Purge failed for a document. documentId={DocumentId} protocol={Protocol} errorType={ErrorType}",
                        candidate.DocumentId,
                        candidate.Protocol,
                        exception.GetType().Name);
                }
            }
        }

        logger.LogInformation("Purge run finished. purged={Purged} failed={Failed}", purged, failedIds.Count);

        return new PurgeResult(purged, failedIds.Count);
    }
}
