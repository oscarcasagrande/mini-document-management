using DocReader.Domain.Audit;

namespace DocReader.Application.Audit;

public interface IAuditLogStore
{
    Task AddAsync(AuditLog entry, CancellationToken ct);
}
