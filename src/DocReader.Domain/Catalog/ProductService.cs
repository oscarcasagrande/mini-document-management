using System.Text.RegularExpressions;
using DocReader.Domain.Storage;

namespace DocReader.Domain.Catalog;

/// <summary>
/// A product or service a document is uploaded for. It is the axis retention, storage and webhooks
/// are configured on, and it is optional on a document.
/// </summary>
public sealed partial class ProductService
{
    public const int MaxCodeLength = 64;
    public const int MaxNameLength = 200;

    private ProductService()
    {
    }

    public Guid Id { get; private init; }

    /// <summary>Unique, stored in upper case: codes are compared without regard to case.</summary>
    public string Code { get; private init; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public bool Active { get; private set; }

    /// <summary>Repository the documents of this product are stored in; null means the default repository.</summary>
    public Guid? StorageRepositoryId { get; private set; }

    public StorageRepository? StorageRepository { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static bool IsValidCode(string? code) => code is not null && CodePattern().IsMatch(code);

    /// <summary>The canonical form of a code, or null when it is not a valid code.</summary>
    public static string? NormalizeCode(string? code)
    {
        var trimmed = code?.Trim();

        return IsValidCode(trimmed) ? trimmed!.ToUpperInvariant() : null;
    }

    public static ProductService Create(Guid id, string code, string name, bool active, DateTimeOffset now)
    {
        var normalized = NormalizeCode(code)
            ?? throw new ArgumentException("The code must have 1 to 64 letters, digits, dots, hyphens or underscores.", nameof(code));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new ProductService
        {
            Id = id,
            Code = normalized,
            Name = name.Trim(),
            Active = active,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void Update(string name, bool active, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name.Trim();
        Active = active;
        UpdatedAt = now;
    }

    /// <summary>Stores the documents of this product in another repository; null goes back to the default one.</summary>
    public void UseStorageRepository(Guid? storageRepositoryId, DateTimeOffset now)
    {
        StorageRepositoryId = storageRepositoryId;
        UpdatedAt = now;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
