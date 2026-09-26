using DocReader.Application.Abstractions;
using DocReader.Domain.Retention;

namespace DocReader.Application.Retention;

/// <summary>Finds the retention policy that applies to a document.</summary>
public sealed class RetentionService(IRetentionPolicyRepository repository)
{
    /// <summary>
    /// The policy that applies to a document of <paramref name="documentType"/> for <paramref name="productServiceId"/>,
    /// by the precedence of <see cref="RetentionPolicyResolver"/>. Null only if the global policy is missing, which
    /// the migration prevents.
    /// </summary>
    public async Task<RetentionPolicy?> ResolveAsync(string? documentType, Guid? productServiceId, CancellationToken ct)
    {
        var policies = await repository.ListAllAsync(ct).ConfigureAwait(false);

        return RetentionPolicyResolver.Resolve(policies, NormalizeType(documentType), productServiceId);
    }

    /// <summary>UNKNOWN is what classification says when it found nothing; for retention it is the same as no type.</summary>
    private static string? NormalizeType(string? documentType) =>
        string.IsNullOrWhiteSpace(documentType) || string.Equals(documentType, "UNKNOWN", StringComparison.Ordinal)
            ? null
            : documentType.Trim();
}
