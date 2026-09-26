using DocReader.Application.Abstractions;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Application.Retention;
using DocReader.Domain.Retention;

namespace DocReader.UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="IRetentionPolicyRepository"/>. It starts with the global policy, as the migration leaves the
/// database, so a use case that resolves retention always finds one.
/// </summary>
public sealed class InMemoryRetentionPolicyStore : IRetentionPolicyRepository
{
    public InMemoryRetentionPolicyStore(int globalDays = 365)
    {
        Items.Add(RetentionPolicy.Create(RetentionPolicy.GlobalPolicyId, null, null, globalDays, DateTimeOffset.UnixEpoch));
    }

    public List<RetentionPolicy> Items { get; } = [];

    public RetentionPolicy Global => Items.Single(policy => policy.IsGlobal);

    public Task<IReadOnlyList<RetentionPolicy>> ListAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RetentionPolicy>>([.. Items]);

    public Task<PagedResult<RetentionPolicy>> ListAsync(RetentionPolicyFilter filter, CancellationToken ct)
    {
        var query = Items.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter.DocumentType))
        {
            query = query.Where(policy => policy.DocumentType == filter.DocumentType.Trim().ToUpperInvariant());
        }

        if (filter.ProductServiceId is { } productServiceId)
        {
            query = query.Where(policy => policy.ProductServiceId == productServiceId);
        }

        var ordered = query.OrderBy(policy => (int)policy.Scope).ToList();

        return Task.FromResult(new PagedResult<RetentionPolicy>(
            [.. ordered.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)],
            filter.Page,
            filter.PageSize,
            ordered.Count));
    }

    public Task<RetentionPolicy?> FindByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(policy => policy.Id == id));

    public Task<RetentionPolicy?> FindByScopeAsync(string? documentType, Guid? productServiceId, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(policy => policy.DocumentType == documentType && policy.ProductServiceId == productServiceId));

    public Task AddAsync(RetentionPolicy policy, CancellationToken ct)
    {
        if (Items.Any(item => item.DocumentType == policy.DocumentType && item.ProductServiceId == policy.ProductServiceId))
        {
            throw new ResourceConflictException("RETENTION_POLICY_EXISTS", "duplicate");
        }

        Items.Add(policy);

        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

    public Task RemoveAsync(RetentionPolicy policy, CancellationToken ct)
    {
        Items.Remove(policy);

        return Task.CompletedTask;
    }
}
