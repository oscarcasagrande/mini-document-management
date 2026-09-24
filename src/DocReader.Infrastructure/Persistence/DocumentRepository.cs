using DocReader.Application.Abstractions;
using DocReader.Application.Documents;
using DocReader.Domain.Documents;
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

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        // Events, jobs and idempotency keys are removed by the cascading foreign keys.
        var affected = await dbContext.Documents
            .Where(document => document.Id == id)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        return affected > 0;
    }

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
