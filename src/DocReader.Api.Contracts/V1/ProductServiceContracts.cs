using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace DocReader.Api.Contracts.V1;

/// <summary>A product or service documents can be linked to.</summary>
/// <param name="Id">Identity.</param>
/// <param name="Code">Unique code, in upper case. Send it as <c>productServiceCode</c> when uploading.</param>
/// <param name="Name">Display name.</param>
/// <param name="Active">An inactive product keeps its documents but refuses new uploads.</param>
/// <param name="StorageRepository">Repository its documents are stored in; null means the default repository.</param>
/// <param name="CreatedAt">Creation instant, in UTC.</param>
/// <param name="UpdatedAt">Last change, in UTC.</param>
public sealed record ProductServiceResponse(
    Guid Id,
    string Code,
    string Name,
    bool Active,
    StorageRepositoryReferenceResponse? StorageRepository,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The product or service a document is linked to.</summary>
/// <param name="Id">Identity.</param>
/// <param name="Code">Unique code.</param>
/// <param name="Name">Display name.</param>
public sealed record ProductServiceReferenceResponse(Guid Id, string Code, string Name);

public sealed class CreateProductServiceRequest
{
    /// <summary>Unique code: 1 to 64 letters, digits, dots, hyphens or underscores, starting with a letter or digit. Stored in upper case and immutable.</summary>
    [Required]
    [MaxLength(64)]
    public string? Code { get; init; }

    [Required]
    [MaxLength(200)]
    public string? Name { get; init; }

    [DefaultValue(true)]
    public bool Active { get; init; } = true;

    /// <summary>Repository the documents of this product are stored in; omit to use the default repository. Must exist and be active.</summary>
    public Guid? StorageRepositoryId { get; init; }
}

public sealed class UpdateProductServiceRequest
{
    [Required]
    [MaxLength(200)]
    public string? Name { get; init; }

    public bool Active { get; init; } = true;

    /// <summary>Repository the documents of this product are stored in; omit (null) to use the default repository. Must exist and be active.</summary>
    public Guid? StorageRepositoryId { get; init; }
}

public sealed class ProductServiceListRequest
{
    [FromQuery(Name = "code")]
    [MaxLength(64)]
    public string? Code { get; init; }

    [FromQuery(Name = "name")]
    [MaxLength(200)]
    public string? Name { get; init; }

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
