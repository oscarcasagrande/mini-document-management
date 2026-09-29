using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace DocReader.Api.Contracts.V1;

/// <summary>A document type classification and extraction consult at runtime.</summary>
/// <param name="Id">Identity.</param>
/// <param name="Code">Unique code, in upper case. Matches the value of <c>detectedType</c>/<c>expectedDocumentType</c> on a document.</param>
/// <param name="Name">Display name.</param>
/// <param name="Schema">The type's JSON Schema: fields, validations and the <c>x-docreader</c> extension.</param>
/// <param name="ClassificationRules">What the classifier reads: <c>{ evidence, counterEvidence, threshold }</c>.</param>
/// <param name="ExtractionRules">Description of what the extractor reads. Informational for the seven built-in types, whose extraction is C# code.</param>
/// <param name="Active">An inactive type is skipped by classification and cannot be assigned to a new document.</param>
/// <param name="IsBuiltIn">One of the seven types migrated from code. Cannot be deleted, only deactivated or edited.</param>
/// <param name="CreatedAt">Creation instant, in UTC.</param>
/// <param name="UpdatedAt">Last change, in UTC.</param>
public sealed record DocumentTypeResponse(
    Guid Id,
    string Code,
    string Name,
    JsonElement Schema,
    JsonElement ClassificationRules,
    JsonElement ExtractionRules,
    bool Active,
    bool IsBuiltIn,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed class CreateDocumentTypeRequest
{
    /// <summary>Unique code: 1 to 64 letters, digits, dots, hyphens or underscores, starting with a letter or digit. Stored in upper case and immutable.</summary>
    [Required]
    [MaxLength(64)]
    public string? Code { get; init; }

    [Required]
    [MaxLength(200)]
    public string? Name { get; init; }

    /// <summary>The type's JSON Schema. Sent and stored as JSON, not as a string.</summary>
    [Required]
    public JsonElement Schema { get; init; }

    /// <summary>What the classifier reads: <c>{ evidence: [{ name, weight, patterns }], counterEvidence: [...], threshold }</c>.</summary>
    [Required]
    public JsonElement ClassificationRules { get; init; }

    /// <summary>Description of what the extractor reads. Send <c>{}</c> when there is nothing to record.</summary>
    [Required]
    public JsonElement ExtractionRules { get; init; }

    [DefaultValue(true)]
    public bool Active { get; init; } = true;
}

public sealed class UpdateDocumentTypeRequest
{
    [Required]
    [MaxLength(200)]
    public string? Name { get; init; }

    [Required]
    public JsonElement Schema { get; init; }

    [Required]
    public JsonElement ClassificationRules { get; init; }

    [Required]
    public JsonElement ExtractionRules { get; init; }

    public bool Active { get; init; } = true;
}

public sealed class DocumentTypeListRequest
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
