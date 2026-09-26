using DocReader.Application.Abstractions;
using DocReader.Application.Classification;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Domain.Retention;
using Microsoft.Extensions.Logging;

namespace DocReader.Application.Retention;

/// <summary>
/// Administration of the retention policies. Changing or creating a policy never touches a document that already
/// has a purge date: applying it to existing documents is an explicit act (reprocessing), not a side effect.
/// </summary>
public sealed class RetentionPolicyService(
    IRetentionPolicyRepository repository,
    IProductServiceRepository productServices,
    TimeProvider timeProvider,
    ILogger<RetentionPolicyService> logger)
{
    /// <summary>The document types a policy can name: the ones the classifier can produce.</summary>
    public static IReadOnlyList<string> KnownDocumentTypes { get; } = [.. DocumentTypeProfile.All.Select(profile => profile.DocumentType)];

    public Task<PagedResult<RetentionPolicy>> ListAsync(RetentionPolicyFilter filter, CancellationToken ct) =>
        repository.ListAsync(filter, ct);

    public async Task<RetentionPolicy> GetAsync(Guid id, CancellationToken ct) =>
        await repository.FindByIdAsync(id, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("retention-policy", id.ToString());

    public async Task<RetentionPolicy> CreateAsync(
        string? documentType,
        Guid? productServiceId,
        int retentionDays,
        CancellationToken ct)
    {
        var type = NormalizeType(documentType);
        ValidateDays(retentionDays);

        if (productServiceId is { } productId
            && await productServices.FindByIdAsync(productId, ct).ConfigureAwait(false) is null)
        {
            throw new UnprocessableRequestException(
                "PRODUCT_SERVICE_NOT_FOUND",
                $"There is no product or service with the id {productId}.");
        }

        if (await repository.FindByScopeAsync(type, productServiceId, ct).ConfigureAwait(false) is not null)
        {
            throw Duplicate(type, productServiceId);
        }

        var now = timeProvider.GetUtcNow();
        var policy = RetentionPolicy.Create(Guid.CreateVersion7(now), type, productServiceId, retentionDays, now);

        await repository.AddAsync(policy, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Retention policy created. retentionPolicyId={RetentionPolicyId} scope={Scope} retentionDays={RetentionDays}",
            policy.Id,
            policy.Scope,
            policy.RetentionDays);

        return policy;
    }

    /// <summary>Changes the duration. The scope is the identity of a policy: to move it, create another and delete this one.</summary>
    public async Task<RetentionPolicy> UpdateAsync(Guid id, int retentionDays, CancellationToken ct)
    {
        var policy = await GetAsync(id, ct).ConfigureAwait(false);
        ValidateDays(retentionDays);

        policy.ChangeRetention(retentionDays, timeProvider.GetUtcNow());
        await repository.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Retention policy updated. retentionPolicyId={RetentionPolicyId} scope={Scope} retentionDays={RetentionDays}",
            policy.Id,
            policy.Scope,
            policy.RetentionDays);

        return policy;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var policy = await GetAsync(id, ct).ConfigureAwait(false);

        if (policy.IsGlobal)
        {
            throw new ResourceConflictException(
                "GLOBAL_RETENTION_POLICY_PROTECTED",
                "The global retention policy cannot be deleted: it is what applies to a document no other policy covers. Change its retentionDays instead.");
        }

        await repository.RemoveAsync(policy, ct).ConfigureAwait(false);

        logger.LogInformation("Retention policy deleted. retentionPolicyId={RetentionPolicyId} scope={Scope}", id, policy.Scope);
    }

    private static string? NormalizeType(string? documentType)
    {
        if (string.IsNullOrWhiteSpace(documentType))
        {
            return null;
        }

        var normalized = documentType.Trim().ToUpperInvariant();

        return KnownDocumentTypes.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : throw new RequestValidationException(
                "INVALID_DOCUMENT_TYPE",
                $"Unknown document type {documentType.Trim()}. Known types: {string.Join(", ", KnownDocumentTypes)}.");
    }

    private static void ValidateDays(int retentionDays)
    {
        if (!RetentionPolicy.IsValidDays(retentionDays))
        {
            throw new RequestValidationException(
                "INVALID_RETENTION_DAYS",
                $"retentionDays must be between {RetentionPolicy.MinimumDays} and {RetentionPolicy.MaximumDays}.");
        }
    }

    private static ResourceConflictException Duplicate(string? documentType, Guid? productServiceId) =>
        documentType is null && productServiceId is null
            ? new ResourceConflictException(
                "RETENTION_POLICY_EXISTS",
                "The global retention policy already exists. There is exactly one: change its retentionDays.")
            : new ResourceConflictException(
                "RETENTION_POLICY_EXISTS",
                "A retention policy for this document type and product or service already exists. Change it instead of creating another.");
}
