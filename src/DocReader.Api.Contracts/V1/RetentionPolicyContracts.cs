using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using DocReader.Domain.Retention;
using Microsoft.AspNetCore.Mvc;

namespace DocReader.Api.Contracts.V1;

/// <summary>How long documents are kept before they are purged, for one scope.</summary>
/// <param name="Id">Identity.</param>
/// <param name="DocumentType">Document type the policy covers, or null for any type.</param>
/// <param name="ProductService">Product or service the policy covers, or null for any.</param>
/// <param name="RetentionDays">Days a document is kept, counted from its upload (or from its last reprocessing).</param>
/// <param name="Scope">GLOBAL, DOCUMENT_TYPE, PRODUCT_SERVICE or DOCUMENT_TYPE_AND_PRODUCT_SERVICE.</param>
/// <param name="IsGlobal">The global policy always exists, covers whatever no other policy covers, and cannot be deleted.</param>
/// <param name="CreatedAt">Creation instant, in UTC.</param>
/// <param name="UpdatedAt">Last change, in UTC.</param>
public sealed record RetentionPolicyResponse(
    Guid Id,
    string? DocumentType,
    ProductServiceReferenceResponse? ProductService,
    int RetentionDays,
    RetentionScope Scope,
    bool IsGlobal,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The purge date of a document and where it came from.</summary>
/// <param name="ExpiresAt">When the document becomes eligible for purge, in UTC.</param>
/// <param name="RetentionDays">Days of retention in force when the date was set.</param>
/// <param name="Policy">The policy that set the date; null when it was deleted since. The date and the days remain.</param>
/// <param name="PurgedAt">When the original file was removed; null while the document is alive.</param>
public sealed record DocumentRetentionResponse(
    DateTimeOffset ExpiresAt,
    int RetentionDays,
    RetentionPolicyReferenceResponse? Policy,
    DateTimeOffset? PurgedAt);

/// <summary>The policy that defined the purge date of a document.</summary>
/// <param name="Id">Identity of the policy.</param>
/// <param name="Scope">What the policy covers.</param>
/// <param name="DocumentType">Document type it covers, or null.</param>
/// <param name="ProductServiceCode">Code of the product or service it covers, or null.</param>
/// <param name="RetentionDays">Days the policy says <em>now</em>; can differ from the days that set the date if it was edited since.</param>
public sealed record RetentionPolicyReferenceResponse(
    Guid Id,
    RetentionScope Scope,
    string? DocumentType,
    string? ProductServiceCode,
    int RetentionDays);

public sealed class CreateRetentionPolicyRequest
{
    /// <summary>Document type the policy covers (BR_CPF_CARD, BR_CIN, BR_CNH, BR_PROOF_OF_ADDRESS, BR_CNPJ_CARD, BR_CCMEI or BR_SOCIAL_CONTRACT); omit for any type.</summary>
    [MaxLength(64)]
    public string? DocumentType { get; init; }

    /// <summary>Id of the product or service the policy covers; omit for any.</summary>
    public Guid? ProductServiceId { get; init; }

    /// <summary>Days a document is kept, from 1 to 36500.</summary>
    [Required]
    [Range(RetentionPolicy.MinimumDays, RetentionPolicy.MaximumDays)]
    public int? RetentionDays { get; init; }
}

public sealed class UpdateRetentionPolicyRequest
{
    /// <summary>Days a document is kept, from 1 to 36500. The scope of a policy cannot change.</summary>
    [Required]
    [Range(RetentionPolicy.MinimumDays, RetentionPolicy.MaximumDays)]
    public int? RetentionDays { get; init; }
}

public sealed class RetentionPolicyListRequest
{
    [FromQuery(Name = "documentType")]
    [MaxLength(64)]
    public string? DocumentType { get; init; }

    [FromQuery(Name = "productServiceId")]
    public Guid? ProductServiceId { get; init; }

    [FromQuery(Name = "page")]
    [Range(1, int.MaxValue)]
    [DefaultValue(1)]
    public int Page { get; init; } = 1;

    [FromQuery(Name = "pageSize")]
    [Range(1, 500)]
    public int? PageSize { get; init; }
}
