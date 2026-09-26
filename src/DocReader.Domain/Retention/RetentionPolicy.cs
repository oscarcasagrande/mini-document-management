using DocReader.Domain.Catalog;

namespace DocReader.Domain.Retention;

/// <summary>
/// How long documents are kept before they are purged. A policy applies to a document type, to a product or
/// service, to both, or (when both are null) to everything. See <see cref="RetentionPolicyResolver"/> for the
/// precedence between them.
/// </summary>
public sealed class RetentionPolicy
{
    public const int MinimumDays = 1;

    /// <summary>One hundred years: past it a typo (36500000) is far likelier than intent.</summary>
    public const int MaximumDays = 36_500;

    /// <summary>Identity of the global policy created by the migration. It always exists and is never deleted.</summary>
    public static readonly Guid GlobalPolicyId = new("00000000-0000-7000-8000-000000000001");

    private RetentionPolicy()
    {
    }

    public Guid Id { get; private init; }

    /// <summary>Document type the policy applies to, or null for any type.</summary>
    public string? DocumentType { get; private init; }

    /// <summary>Product or service the policy applies to, or null for any.</summary>
    public Guid? ProductServiceId { get; private init; }

    public ProductService? ProductService { get; private set; }

    public int RetentionDays { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsGlobal => DocumentType is null && ProductServiceId is null;

    public RetentionScope Scope => (DocumentType is not null, ProductServiceId is not null) switch
    {
        (true, true) => RetentionScope.DocumentTypeAndProductService,
        (false, true) => RetentionScope.ProductService,
        (true, false) => RetentionScope.DocumentType,
        _ => RetentionScope.Global
    };

    public static bool IsValidDays(int days) => days is >= MinimumDays and <= MaximumDays;

    public static RetentionPolicy Create(
        Guid id,
        string? documentType,
        Guid? productServiceId,
        int retentionDays,
        DateTimeOffset now)
    {
        if (!IsValidDays(retentionDays))
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays), $"Retention must be between {MinimumDays} and {MaximumDays} days.");
        }

        return new RetentionPolicy
        {
            Id = id,
            DocumentType = documentType,
            ProductServiceId = productServiceId,
            RetentionDays = retentionDays,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>Only the duration changes: the scope of a policy is its identity, so a different scope is a different policy.</summary>
    public void ChangeRetention(int retentionDays, DateTimeOffset now)
    {
        if (!IsValidDays(retentionDays))
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays), $"Retention must be between {MinimumDays} and {MaximumDays} days.");
        }

        RetentionDays = retentionDays;
        UpdatedAt = now;
    }
}
