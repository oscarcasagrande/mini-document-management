using DocReader.Domain.Catalog;

namespace DocReader.Domain.Storage;

/// <summary>
/// A place documents are stored, with its provider and the (encrypted) settings to reach it. Exactly one
/// repository is the default; a product or service can name another, and a document remembers the one it was
/// stored in, so a later change of the default never strands an existing file.
/// </summary>
public sealed class StorageRepository
{
    public const int MaxCodeLength = ProductService.MaxCodeLength;
    public const int MaxNameLength = 200;

    /// <summary>Identity of the file system repository the migration creates as the default.</summary>
    public static readonly Guid DefaultRepositoryId = new("00000000-0000-7000-8000-0000000000d1");

    private StorageRepository()
    {
    }

    public Guid Id { get; private init; }

    /// <summary>Unique, in upper case.</summary>
    public string Code { get; private init; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Fixed at creation: moving files between providers is a migration, not an edit.</summary>
    public StorageProvider Provider { get; private init; }

    /// <summary>
    /// The connection settings as an encrypted envelope (JSON), or null when there are none. The domain never sees
    /// the plain text: protecting and unprotecting is the job of the application layer.
    /// </summary>
    public string? EncryptedConnectionConfig { get; private set; }

    public bool IsDefault { get; private set; }

    /// <summary>An inactive repository keeps serving its documents but takes no new ones.</summary>
    public bool Active { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Whether the adapter for the provider exists. Azure and S3 are registered, not implemented.</summary>
    public bool IsImplemented => IsProviderImplemented(Provider);

    public static bool IsProviderImplemented(StorageProvider provider) =>
        provider is StorageProvider.FileSystem or StorageProvider.Database;

    public static string? NormalizeCode(string? code) => ProductService.NormalizeCode(code);

    public static StorageRepository Create(
        Guid id,
        string code,
        string name,
        StorageProvider provider,
        string? encryptedConnectionConfig,
        bool isDefault,
        bool active,
        DateTimeOffset now)
    {
        var normalized = NormalizeCode(code)
            ?? throw new ArgumentException("The code must have 1 to 64 letters, digits, dots, hyphens or underscores.", nameof(code));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new StorageRepository
        {
            Id = id,
            Code = normalized,
            Name = name.Trim(),
            Provider = provider,
            EncryptedConnectionConfig = encryptedConnectionConfig,
            IsDefault = isDefault,
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

    public void ReplaceConnectionConfig(string? encryptedConnectionConfig, DateTimeOffset now)
    {
        EncryptedConnectionConfig = encryptedConnectionConfig;
        UpdatedAt = now;
    }

    public void SetDefault(bool isDefault, DateTimeOffset now)
    {
        IsDefault = isDefault;
        UpdatedAt = now;
    }
}
