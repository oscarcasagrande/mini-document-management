using DocReader.Application.Audit;
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
}
