namespace DocReader.Domain.Retention;

/// <summary>
/// Picks the policy that applies to a document. Precedence, most specific first:
/// type and product together, then the product alone, then the type alone, then the global policy.
/// A product policy beats a type policy on purpose: the product is what the customer contracted, the type is
/// only what the document turned out to be.
/// </summary>
public static class RetentionPolicyResolver
{
    /// <returns>The applicable policy, or null when there is not even a global one.</returns>
    public static RetentionPolicy? Resolve(
        IEnumerable<RetentionPolicy> policies,
        string? documentType,
        Guid? productServiceId)
    {
        var candidates = policies as IReadOnlyCollection<RetentionPolicy> ?? [.. policies];

        return Find(candidates, documentType, productServiceId)
            ?? Find(candidates, null, productServiceId)
            ?? Find(candidates, documentType, null)
            ?? Find(candidates, null, null);
    }

    private static RetentionPolicy? Find(IEnumerable<RetentionPolicy> policies, string? documentType, Guid? productServiceId)
    {
        // A lookup on a null dimension is only meaningful when the other one is set (or both are null, the global one).
        return policies.FirstOrDefault(policy =>
            string.Equals(policy.DocumentType, documentType, StringComparison.Ordinal)
            && policy.ProductServiceId == productServiceId);
    }
}
