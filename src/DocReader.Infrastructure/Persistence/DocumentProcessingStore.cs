using DocReader.Application.Abstractions;
using DocReader.Domain.Documents;
using DocReader.Domain.Extractions;
using DocReader.Domain.Processing;
using Microsoft.EntityFrameworkCore;

namespace DocReader.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of the worker writes. Every operation starts from a clean change tracker:
/// the queue updates the same rows with raw SQL, so an entity cached by an earlier call would be stale.
/// </summary>
public sealed class DocumentProcessingStore(DocReaderDbContext dbContext, TimeProvider timeProvider)
    : IDocumentProcessingStore
{
    public Task<Document?> FindDocumentAsync(Guid documentId, CancellationToken ct) =>
        dbContext.Documents.AsNoTracking().FirstOrDefaultAsync(document => document.Id == documentId, ct);

    public async Task AdvanceStageAsync(
        Guid documentId,
        DocumentStatus stage,
        string eventType,
        string? details,
        CancellationToken ct)
    {
        dbContext.ChangeTracker.Clear();

        var document = await LoadForUpdateAsync(documentId, ct).ConfigureAwait(false);
        document.AdvanceTo(stage, eventType, timeProvider.GetUtcNow(), details);

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordProgressAsync(Guid documentId, string eventType, string? details, CancellationToken ct)
    {
        dbContext.ChangeTracker.Clear();

        var document = await LoadForUpdateAsync(documentId, ct).ConfigureAwait(false);
        document.RecordProgress(eventType, timeProvider.GetUtcNow(), details);

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> CompleteAsync(
        ProcessingJob job,
        DocumentExtraction extraction,
        string detectedDocumentType,
        decimal? classificationConfidence,
        string? classificationDetails,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        // The retrying execution strategy has to own the transaction, otherwise a retry would replay
        // only part of it.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async cancellationToken =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            // The fence: only the attempt that still owns the job may complete it. Doing it first
            // means a worker that lost its lock writes no result at all.
            var completed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE processing_jobs
                 SET status = 'COMPLETED', finished_at = {now}, locked_at = NULL, locked_by = NULL,
                     error_code = NULL, error_message = NULL, pages_completed = COALESCE(page_count, pages_completed)
                 WHERE id = {job.Id} AND status = 'RUNNING' AND attempt_count = {job.AttemptCount};
                 """,
                cancellationToken).ConfigureAwait(false);

            if (completed == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            // A retry of the same job (after a crash between persist and acknowledge) must not leave
            // two extractions for one attempt.
            await dbContext.Extractions
                .Where(existing => existing.ProcessingJobId == job.Id)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);

            var document = await LoadForUpdateAsync(job.DocumentId, cancellationToken).ConfigureAwait(false);
            document.RecordClassification(detectedDocumentType, classificationConfidence, now, classificationDetails);
            document.MarkCompleted(now);

            dbContext.Extractions.Add(extraction);

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return true;
        }, ct).ConfigureAwait(false);
    }

    private async Task<Document> LoadForUpdateAsync(Guid documentId, CancellationToken ct) =>
        await dbContext.Documents.FirstOrDefaultAsync(document => document.Id == documentId, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Document {documentId} no longer exists.");
}
