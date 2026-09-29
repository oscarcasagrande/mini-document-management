using DocReader.Application.Documents;
using DocReader.Domain.Audit;

namespace DocReader.Application.Audit;

public interface IAuditLogStore
{
    Task AddAsync(AuditLog entry, CancellationToken ct);

    Task<PagedResult<AuditLog>> ListAsync(AuditLogFilter filter, CancellationToken ct);
}
