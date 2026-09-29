using DocReader.Application.Audit;
using DocReader.Application.Documents;
using DocReader.Domain.Audit;

namespace DocReader.UnitTests.Fakes;

/// <summary>Keeps every recorded entry in memory, so a test can assert who/what/when without a database.</summary>
public sealed class InMemoryAuditLogStore : IAuditLogStore
{
    public List<AuditLog> Entries { get; } = [];

    public Task AddAsync(AuditLog entry, CancellationToken ct)
    {
        Entries.Add(entry);

        return Task.CompletedTask;
    }

    public Task<PagedResult<AuditLog>> ListAsync(AuditLogFilter filter, CancellationToken ct)
    {
        var query = Entries.AsEnumerable();

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

        var ordered = query.OrderByDescending(entry => entry.OccurredAt).ToList();

        return Task.FromResult(new PagedResult<AuditLog>(
            [.. ordered.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)],
            filter.Page,
            filter.PageSize,
            ordered.Count));
    }
}
