using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocReader.Domain.Catalog;

/// <summary>
/// A document type the pipeline can classify and extract: its JSON Schema, the evidence the classifier
/// weighs and a description of what the extractor reads. Classification and extraction consult this table
/// at runtime, so a rule edited here applies to the next document, without a deploy. The seven built-in
/// types (<see cref="IsBuiltIn"/>) came from code; their <c>code</c> still matches the extractor class that
/// implements them.
/// </summary>
public sealed partial class DocumentType
{
    public const int MaxCodeLength = 64;
    public const int MaxNameLength = 200;

    private DocumentType()
    {
    }

    public Guid Id { get; private init; }

    /// <summary>Unique, stored in upper case: matches the classifier's and the extractors' type codes.</summary>
    public string Code { get; private init; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>The type's JSON Schema: fields, validations and the <c>x-docreader</c> extension.</summary>
    public string SchemaJson { get; private set; } = string.Empty;

    /// <summary>What the weighted-evidence classifier reads: <c>{ evidence, counterEvidence, threshold }</c>.</summary>
    public string ClassificationRulesJson { get; private set; } = string.Empty;

    /// <summary>Description of what the extractor reads for this type. Informational: the seven built-in
    /// extractors are C# classes, not interpreted from this JSON.</summary>
    public string ExtractionRulesJson { get; private set; } = string.Empty;

    /// <summary>An inactive type is skipped by classification and cannot be assigned to a new document.</summary>
    public bool Active { get; private set; }

    /// <summary>One of the seven types migrated from code at Etapa 6. Cannot be deleted, only deactivated.</summary>
    public bool IsBuiltIn { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static bool IsValidCode(string? code) => code is not null && CodePattern().IsMatch(code);

    /// <summary>The canonical form of a code, or null when it is not a valid code.</summary>
    public static string? NormalizeCode(string? code)
    {
        var trimmed = code?.Trim();

        return IsValidCode(trimmed) ? trimmed!.ToUpperInvariant() : null;
    }

    public static bool IsWellFormedJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static DocumentType Create(
        Guid id,
        string code,
        string name,
        string schemaJson,
        string classificationRulesJson,
        string extractionRulesJson,
        bool active,
        bool isBuiltIn,
        DateTimeOffset now)
    {
        var normalized = NormalizeCode(code)
            ?? throw new ArgumentException("The code must have 1 to 64 letters, digits, dots, hyphens or underscores.", nameof(code));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RequireJson(schemaJson, nameof(schemaJson));
        RequireJson(classificationRulesJson, nameof(classificationRulesJson));
        RequireJson(extractionRulesJson, nameof(extractionRulesJson));

        return new DocumentType
        {
            Id = id,
            Code = normalized,
            Name = name.Trim(),
            SchemaJson = schemaJson,
            ClassificationRulesJson = classificationRulesJson,
            ExtractionRulesJson = extractionRulesJson,
            Active = active,
            IsBuiltIn = isBuiltIn,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void Update(
        string name,
        string schemaJson,
        string classificationRulesJson,
        string extractionRulesJson,
        bool active,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RequireJson(schemaJson, nameof(schemaJson));
        RequireJson(classificationRulesJson, nameof(classificationRulesJson));
        RequireJson(extractionRulesJson, nameof(extractionRulesJson));

        Name = name.Trim();
        SchemaJson = schemaJson;
        ClassificationRulesJson = classificationRulesJson;
        ExtractionRulesJson = extractionRulesJson;
        Active = active;
        UpdatedAt = now;
    }

    private static void RequireJson(string json, string paramName)
    {
        if (!IsWellFormedJson(json))
        {
            throw new ArgumentException("Must be well-formed JSON.", paramName);
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
