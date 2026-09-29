using DocReader.Application.Audit;
using DocReader.Domain.Audit;

namespace DocReader.Infrastructure.Persistence;

public sealed class EfAuditLogStore(DocReaderDbContext dbContext) : IAuditLogStore
{
    public async Task AddAsync(AuditLog entry, CancellationToken ct)
    {
        dbContext.AuditLogs.Add(entry);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
