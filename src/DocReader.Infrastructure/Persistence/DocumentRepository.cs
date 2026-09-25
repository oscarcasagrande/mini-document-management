using DocReader.Application.Abstractions;
using DocReader.Application.Documents;
using DocReader.Domain.Documents;
using DocReader.Domain.Extractions;
using DocReader.Domain.Idempotency;
using DocReader.Domain.Processing;
using Microsoft.EntityFrameworkCore;

namespace DocReader.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of the document persistence. All SQL is parameterized by the provider.
/// </summary>
public sealed class DocumentRepository(DocReaderDbContext dbContext) : IDocumentRepository
{
    public async Task AcceptAsync(
        Document document,
        ProcessingJob job,
        IdempotencyRecord? idempotencyRecord,
        CancellationToken ct)
    {
        // The retrying execution strategy has to own the transaction, otherwise a retry would replay
        // only part of it.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async cancellationToken =>
        {
            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            dbContext.Documents.Add(document);
            dbContext.ProcessingJobs.Add(job);

            if (idempotencyRecord is not null)
            {
                dbContext.IdempotencyKeys.Add(idempotencyRecord);
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public Task<Document?> FindByIdAsync(Guid id, bool includeEvents, CancellationToken ct) =>
        BaseQuery(includeEvents).FirstOrDefaultAsync(document => document.Id == id, ct);

    public Task<Document?> FindByProtocolAsync(string protocol, bool includeEvents, CancellationToken ct) =>
        BaseQuery(includeEvents).FirstOrDefaultAsync(document => document.Protocol == protocol, ct);

    public async Task<PagedResult<Document>> ListAsync(DocumentListFilter filter, CancellationToken ct)
    {
        var query = dbContext.Documents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.Protocol))
        {
            var pattern = ToContainsPattern(filter.Protocol);
            query = query.Where(document => EF.Functions.ILike(document.Protocol, pattern, EscapeCharacter));
        }

        if (!string.IsNullOrWhiteSpace(filter.FileName))
        {
            var pattern = ToContainsPattern(filter.FileName);
            query = query.Where(document => EF.Functions.ILike(document.OriginalFileName, pattern, EscapeCharacter));
        }

        if (!string.IsNullOrWhiteSpace(filter.DocumentType))
        {
            var documentType = filter.DocumentType.Trim();
            query = query.Where(document =>
                document.DetectedDocumentType == documentType || document.ExpectedDocumentType == documentType);
        }

        if (filter.Channel is { } channel)
        {
            query = query.Where(document => document.UploadChannel == channel);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(document => document.Status == status);
        }

        if (filter.UploadedFrom is { } from)
        {
            query = query.Where(document => document.UploadedAt >= from);
        }

        if (filter.UploadedTo is { } to)
        {
            query = query.Where(document => document.UploadedAt <= to);
        }

        var totalCount = await query.LongCountAsync(ct).ConfigureAwait(false);

        var items = await query
            .OrderByDescending(document => document.UploadedAt)
            .ThenByDescending(document => document.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<Document>(items, filter.Page, filter.PageSize, totalCount);
    }

    public Task<ProcessingJob?> FindLatestJobAsync(Guid documentId, CancellationToken ct) =>
        dbContext.ProcessingJobs
            .AsNoTracking()
            .Where(job => job.DocumentId == documentId)
            .OrderByDescending(job => job.CreatedAt)
            .ThenByDescending(job => job.Id)
            .FirstOrDefaultAsync(ct);

    public Task<ExtractionSummary?> FindLatestExtractionSummaryAsync(Guid documentId, CancellationToken ct) =>
        SummariesOf(documentId).FirstOrDefaultAsync(ct);

    public async Task<ExtractionResultView?> FindLatestExtractionResultAsync(Guid documentId, CancellationToken ct)
    {
        var summary = await SummariesOf(documentId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (summary is null)
        {
            return null;
        }

        // Projected on purpose: the entity would also load the raw OCR payload of every field's parent.
        var fields = await dbContext.ExtractedFields
            .AsNoTracking()
            .Where(field => field.ExtractionId == summary.ExtractionId)
            .OrderBy(field => field.FieldPath)
            .Select(field => new ExtractedFieldView(
                field.FieldPath,
                field.RawValue,
                field.NormalizedValue,
                field.Confidence,
                field.PageNumber,
                field.BoundingBoxJson,
                field.ValidationStatus,
                field.ValidationMessagesJson))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new ExtractionResultView(summary, fields);
    }

    public async Task<ExtractionTextView?> FindLatestExtractionTextAsync(Guid documentId, CancellationToken ct)
    {
        var summary = await SummariesOf(documentId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (summary is null)
        {
            return null;
        }

        var pageTexts = await dbContext.Extractions
            .AsNoTracking()
            .Where(extraction => extraction.Id == summary.ExtractionId)
            .Select(extraction => extraction.PageTextsJson)
            .FirstAsync(ct)
            .ConfigureAwait(false);

        return new ExtractionTextView(summary, pageTexts);
    }

    public async Task<ExtractionOcrView?> FindLatestExtractionOcrAsync(Guid documentId, CancellationToken ct)
    {
        var summary = await SummariesOf(documentId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (summary is null)
        {
            return null;
        }

        var stored = await dbContext.Extractions
            .AsNoTracking()
            .Where(extraction => extraction.Id == summary.ExtractionId)
            .Select(extraction => new { extraction.RawOcrResultJson, extraction.PageTextsJson })
            .FirstAsync(ct)
            .ConfigureAwait(false);

        return new ExtractionOcrView(summary, stored.RawOcrResultJson, stored.PageTextsJson);
    }

    public async Task<ReprocessOutcome> QueueReprocessingAsync(Guid documentId, DateTimeOffset now, CancellationToken ct)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async cancellationToken =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            // Serializes concurrent requests for the same document: the second one waits here and then
            // sees the job the first one created.
            var locked = await dbContext.Database
                .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM documents WHERE id = {documentId} FOR UPDATE")
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (locked.Count == 0)
            {
                return ReprocessOutcome.NotFound;
            }

            var hasActiveJob = await dbContext.ProcessingJobs
                .AnyAsync(
                    job => job.DocumentId == documentId &&
                           (job.Status == ProcessingJobStatus.Pending || job.Status == ProcessingJobStatus.Running),
                    cancellationToken)
                .ConfigureAwait(false);

            var document = await dbContext.Documents
                .FirstAsync(candidate => candidate.Id == documentId, cancellationToken)
                .ConfigureAwait(false);

            if (hasActiveJob || document.Status is not (DocumentStatus.Stored or DocumentStatus.Failed or DocumentStatus.Completed))
            {
                return ReprocessOutcome.Conflict;
            }

            document.MarkQueued(now, "REPROCESS_REQUESTED");
            dbContext.ProcessingJobs.Add(ProcessingJob.CreateForDocument(documentId, now));

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return ReprocessOutcome.Queued;
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        // Events, jobs and idempotency keys are removed by the cascading foreign keys.
        var affected = await dbContext.Documents
            .Where(document => document.Id == id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        return affected > 0;
    }

    private IQueryable<ExtractionSummary> SummariesOf(Guid documentId) =>
        dbContext.Extractions
            .AsNoTracking()
            .Where(extraction => extraction.DocumentId == documentId)
            .OrderByDescending(extraction => extraction.CreatedAt)
            .Select(extraction => new ExtractionSummary(
                extraction.Id,
                extraction.OcrProvider,
                extraction.OcrModelVersion,
                extraction.ClassifierVersion,
                extraction.ExtractorVersion,
                extraction.SchemaVersion,
                extraction.OverallConfidence,
                extraction.CreatedAt));

    private IQueryable<Document> BaseQuery(bool includeEvents)
    {
        var query = dbContext.Documents.AsNoTracking();
        return includeEvents ? query.Include(document => document.Events) : query;
    }

    private const string EscapeCharacter = "\\";

    /// <summary>
    /// Builds a contains pattern with the wildcard characters of LIKE escaped, so a value such as
    /// <c>100%</c> is matched literally instead of turning into a wildcard.
    /// </summary>
    private static string ToContainsPattern(string value)
    {
        var escaped = value.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

        return $"%{escaped}%";
    }
}
