using DocReader.Application.Abstractions;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Application.Retention;
using DocReader.Domain.Retention;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DocReader.Infrastructure.Persistence;

public sealed class RetentionPolicyRepository(DocReaderDbContext dbContext) : IRetentionPolicyRepository
{
    public async Task<IReadOnlyList<RetentionPolicy>> ListAllAsync(CancellationToken ct) =>
        await dbContext.RetentionPolicies.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

    public async Task<PagedResult<RetentionPolicy>> ListAsync(RetentionPolicyFilter filter, CancellationToken ct)
    {
        var query = dbContext.RetentionPolicies.AsNoTracking().Include(policy => policy.ProductService).AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.DocumentType))
        {
            var documentType = filter.DocumentType.Trim().ToUpperInvariant();
            query = query.Where(policy => policy.DocumentType == documentType);
        }

        if (filter.ProductServiceId is { } productServiceId)
        {
            query = query.Where(policy => policy.ProductServiceId == productServiceId);
        }

        var totalCount = await query.LongCountAsync(ct).ConfigureAwait(false);

        // Most general first, so the global policy leads the list and the specific ones follow.
        var items = await query
            .OrderBy(policy => (policy.DocumentType != null ? 1 : 0) + (policy.ProductServiceId != null ? 2 : 0))
            .ThenBy(policy => policy.DocumentType)
            .ThenBy(policy => policy.ProductService!.Code)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<RetentionPolicy>(items, filter.Page, filter.PageSize, totalCount);
    }

    public Task<RetentionPolicy?> FindByIdAsync(Guid id, CancellationToken ct) =>
        dbContext.RetentionPolicies.Include(policy => policy.ProductService).FirstOrDefaultAsync(policy => policy.Id == id, ct);

    public Task<RetentionPolicy?> FindByScopeAsync(string? documentType, Guid? productServiceId, CancellationToken ct) =>
        dbContext.RetentionPolicies.AsNoTracking().FirstOrDefaultAsync(
            policy => policy.DocumentType == documentType && policy.ProductServiceId == productServiceId,
            ct);

    public async Task AddAsync(RetentionPolicy policy, CancellationToken ct)
    {
        dbContext.RetentionPolicies.Add(policy);

        try
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.Entry(policy).State = EntityState.Detached;

            throw new ResourceConflictException(
                "RETENTION_POLICY_EXISTS",
                "A retention policy for this scope already exists. Change it instead of creating another.");
        }

        await dbContext.Entry(policy).Reference(item => item.ProductService).LoadAsync(ct).ConfigureAwait(false);
    }

    public Task SaveChangesAsync(CancellationToken ct) => dbContext.SaveChangesAsync(ct);

    public async Task RemoveAsync(RetentionPolicy policy, CancellationToken ct)
    {
        dbContext.RetentionPolicies.Remove(policy);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
