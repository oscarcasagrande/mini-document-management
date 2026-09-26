using DocReader.Application.Documents;
using DocReader.Application.Retention;
using DocReader.Domain.Retention;

namespace DocReader.Application.Abstractions;

/// <summary>Persistence of the retention policies. Finds by id return tracked entities, so a change is saved with <see cref="SaveChangesAsync"/>.</summary>
public interface IRetentionPolicyRepository
{
    /// <summary>Every policy, untracked. The table is small by nature (a handful of rows), and resolving a document reads all of it.</summary>
    Task<IReadOnlyList<RetentionPolicy>> ListAllAsync(CancellationToken ct);

    Task<PagedResult<RetentionPolicy>> ListAsync(RetentionPolicyFilter filter, CancellationToken ct);

    Task<RetentionPolicy?> FindByIdAsync(Guid id, CancellationToken ct);

    Task<RetentionPolicy?> FindByScopeAsync(string? documentType, Guid? productServiceId, CancellationToken ct);

    /// <exception cref="Errors.ResourceConflictException">A policy with the same scope already exists.</exception>
    Task AddAsync(RetentionPolicy policy, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);

    Task RemoveAsync(RetentionPolicy policy, CancellationToken ct);
}
