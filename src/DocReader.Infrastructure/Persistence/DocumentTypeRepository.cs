using DocReader.Application.Abstractions;
using DocReader.Application.Catalog;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocReader.Infrastructure.Persistence;

public sealed class DocumentTypeRepository(DocReaderDbContext dbContext) : IDocumentTypeRepository
{
    public Task<DocumentType?> FindByIdAsync(Guid id, CancellationToken ct) =>
        dbContext.DocumentTypes.FirstOrDefaultAsync(documentType => documentType.Id == id, ct);

    public Task<DocumentType?> FindByCodeAsync(string code, CancellationToken ct) =>
        dbContext.DocumentTypes.FirstOrDefaultAsync(documentType => documentType.Code == code, ct);

    public async Task<PagedResult<DocumentType>> ListAsync(DocumentTypeFilter filter, CancellationToken ct)
    {
        var query = dbContext.DocumentTypes.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Code))
        {
            var pattern = Like.Contains(filter.Code);
            query = query.Where(documentType => EF.Functions.ILike(documentType.Code, pattern, Like.Escape));
        }

        if (!string.IsNullOrWhiteSpace(filter.Name))
        {
            var pattern = Like.Contains(filter.Name);
            query = query.Where(documentType => EF.Functions.ILike(documentType.Name, pattern, Like.Escape));
        }

        if (filter.Active is { } active)
        {
            query = query.Where(documentType => documentType.Active == active);
        }

        var totalCount = await query.LongCountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderBy(documentType => documentType.Code)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<DocumentType>(items, filter.Page, filter.PageSize, totalCount);
    }

    public async Task<IReadOnlyList<DocumentType>> ListActiveAsync(CancellationToken ct) =>
        await dbContext.DocumentTypes
            .AsNoTracking()
            .Where(documentType => documentType.Active)
            .OrderBy(documentType => documentType.CreatedAt)
            .ThenBy(documentType => documentType.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task AddAsync(DocumentType documentType, CancellationToken ct)
    {
        dbContext.DocumentTypes.Add(documentType);

        try
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.Entry(documentType).State = EntityState.Detached;

            throw new ResourceConflictException(
                "DOCUMENT_TYPE_CODE_EXISTS",
                $"A document type with the code {documentType.Code} already exists.");
        }
    }

    public Task SaveChangesAsync(CancellationToken ct) => dbContext.SaveChangesAsync(ct);

    public async Task<bool> IsReferencedAsync(string code, CancellationToken ct) =>
        await dbContext.Documents
            .AnyAsync(document => document.DetectedDocumentType == code || document.ExpectedDocumentType == code, ct)
            .ConfigureAwait(false)
        || await dbContext.RetentionPolicies.AnyAsync(policy => policy.DocumentType == code, ct).ConfigureAwait(false);

    public async Task RemoveAsync(DocumentType documentType, CancellationToken ct)
    {
        dbContext.DocumentTypes.Remove(documentType);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
