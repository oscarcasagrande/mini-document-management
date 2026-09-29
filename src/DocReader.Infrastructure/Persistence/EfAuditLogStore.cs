using DocReader.Application.Audit;
using DocReader.Application.Documents;
using DocReader.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace DocReader.Infrastructure.Persistence;

public sealed class EfAuditLogStore(DocReaderDbContext dbContext) : IAuditLogStore
{
    public async Task AddAsync(AuditLog entry, CancellationToken ct)
    {
        dbContext.AuditLogs.Add(entry);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<PagedResult<AuditLog>> ListAsync(AuditLogFilter filter, CancellationToken ct)
    {
        var query = dbContext.AuditLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.UserId))
        {
            query = query.Where(entry => entry.UserId == filter.UserId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            query = query.Where(entry => entry.Action == filter.Action);
        }

        if (filter.From is { } from)
        {
            query = query.Where(entry => entry.OccurredAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(entry => entry.OccurredAt <= to);
        }

        var totalCount = await query.LongCountAsync(ct).ConfigureAwait(false);

        // Newest first, like every other listing endpoint of the API.
        var items = await query
            .OrderByDescending(entry => entry.OccurredAt)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<AuditLog>(items, filter.Page, filter.PageSize, totalCount);
    }
}
