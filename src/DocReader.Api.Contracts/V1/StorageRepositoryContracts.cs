using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using DocReader.Domain.Storage;
using Microsoft.AspNetCore.Mvc;

namespace DocReader.Api.Contracts.V1;

/// <summary>A place documents are stored. The connection settings are secrets and are never returned.</summary>
/// <param name="Id">Identity.</param>
/// <param name="Code">Unique code, in upper case.</param>
/// <param name="Name">Display name.</param>
/// <param name="Provider">FILE_SYSTEM, DATABASE, AZURE_BLOB_STORAGE or AWS_S3.</param>
/// <param name="IsDefault">Exactly one repository is the default; it stores the documents of every product that names none.</param>
/// <param name="Active">An inactive repository keeps serving its documents but takes no new ones.</param>
/// <param name="HasConnectionConfig">Whether connection settings are stored. Their content is never shown.</param>
/// <param name="IsImplemented">False for the providers that are registered but whose adapter does not exist yet (AZURE_BLOB_STORAGE, AWS_S3): uploading to them answers 501.</param>
/// <param name="CreatedAt">Creation instant, in UTC.</param>
/// <param name="UpdatedAt">Last change, in UTC.</param>
public sealed record StorageRepositoryResponse(
    Guid Id,
    string Code,
    string Name,
    StorageProvider Provider,
    bool IsDefault,
    bool Active,
    bool HasConnectionConfig,
    bool IsImplemented,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The storage repository a product or service stores its documents in.</summary>
/// <param name="Id">Identity.</param>
/// <param name="Code">Unique code.</param>
/// <param name="Name">Display name.</param>
public sealed record StorageRepositoryReferenceResponse(Guid Id, string Code, string Name);

public sealed class CreateStorageRepositoryRequest
{
    /// <summary>Unique code: 1 to 64 letters, digits, dots, hyphens or underscores. Stored in upper case and immutable.</summary>
    [Required]
    [MaxLength(64)]
    public string? Code { get; init; }

    [Required]
    [MaxLength(200)]
    public string? Name { get; init; }

    /// <summary>FILE_SYSTEM, DATABASE, AZURE_BLOB_STORAGE or AWS_S3. Fixed once created.</summary>
    [Required]
    public StorageProvider? Provider { get; init; }

    /// <summary>
    /// Provider settings, a flat object of strings, encrypted before it is stored. FILE_SYSTEM takes an optional
    /// <c>directory</c> (relative to the storage root); DATABASE takes none; AZURE_BLOB_STORAGE needs
    /// <c>connectionString</c> and <c>container</c>; AWS_S3 needs <c>bucket</c>, <c>accessKeyId</c> and
    /// <c>secretAccessKey</c> and takes <c>region</c> and <c>serviceUrl</c>.
    /// </summary>
    public Dictionary<string, string?>? ConnectionConfig { get; init; }

    /// <summary>Makes this the default repository; the previous default stops being one. Not allowed for a provider that is not implemented, or inactive.</summary>
    [DefaultValue(false)]
    public bool IsDefault { get; init; }

    [DefaultValue(true)]
    public bool Active { get; init; } = true;
}

public sealed class UpdateStorageRepositoryRequest
{
    [Required]
    [MaxLength(200)]
    public string? Name { get; init; }

    public bool Active { get; init; } = true;

    /// <summary>true makes this the default (the previous one stops being it); omit to leave it as is. false is refused for the current default.</summary>
    public bool? IsDefault { get; init; }

    /// <summary>
    /// Partial update of the settings: a key with a string sets it, a key with <c>null</c> removes it, the keys not sent
    /// stay as they are. Omit the whole object to leave the settings alone.
    /// </summary>
    public Dictionary<string, string?>? ConnectionConfig { get; init; }
}

public sealed class StorageRepositoryListRequest
{
    [FromQuery(Name = "code")]
    [MaxLength(64)]
    public string? Code { get; init; }

    [FromQuery(Name = "provider")]
    public StorageProvider? Provider { get; init; }

    [FromQuery(Name = "active")]
    public bool? Active { get; init; }

    [FromQuery(Name = "page")]
    [Range(1, int.MaxValue)]
    [DefaultValue(1)]
    public int Page { get; init; } = 1;

    [FromQuery(Name = "pageSize")]
    [Range(1, 500)]
    public int? PageSize { get; init; }
}
