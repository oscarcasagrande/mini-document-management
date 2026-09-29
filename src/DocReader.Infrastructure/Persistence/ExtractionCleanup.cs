using DocReader.Domain.Documents;
using Microsoft.EntityFrameworkCore;

namespace DocReader.Infrastructure.Persistence;

/// <summary>
/// Deletes a document's extraction rows (OCR text, fields and structured result) and reports what kind of content
/// they held, for the <c>deleted=</c> list of whichever timeline event records the removal. Shared by a retention
/// purge (<see cref="DocumentRepository.MarkPurgedAsync"/>) and a GDPR/LGPD deletion execution
/// (<c>GdprDeletionRequestRepository.MarkExecutedAsync</c>): both destroy exactly the same content, only the
/// reason and the event type on the document differ, so the DB mechanics live here once.
/// </summary>
internal static class ExtractionCleanup
{
    public static async Task<List<string>> DeleteExtractionsAsync(DocReaderDbContext dbContext, Guid documentId, CancellationToken ct)
    {
        var extractions = await dbContext.Extractions
            .AsNoTracking()
            .Where(extraction => extraction.DocumentId == documentId)
            .Select(extraction => new { HasContent = extraction.Fields.Any() || extraction.StructuredResultJson != null })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var deleted = new List<string> { PurgedContent.File };
        if (extractions.Count > 0)
        {
            deleted.Add(PurgedContent.OcrText);
        }

        if (extractions.Any(extraction => extraction.HasContent))
        {
            deleted.Add(PurgedContent.ExtractedFields);
        }

        await dbContext.Extractions
            .Where(extraction => extraction.DocumentId == documentId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        return deleted;
    }
}
