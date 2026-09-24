namespace DocReader.Application.Abstractions;

/// <summary>
/// Structured fields produced by an extractor.
/// </summary>
/// <param name="DocumentType">Type the extractor is responsible for.</param>
/// <param name="SchemaVersion">Version of the JSON Schema used.</param>
/// <param name="OverallConfidence">Aggregated confidence between 0 and 1.</param>
/// <param name="Fields">Field path to extracted value.</param>
public sealed record StructuredExtraction(
    string DocumentType,
    int SchemaVersion,
    decimal? OverallConfidence,
    IReadOnlyDictionary<string, ExtractedFieldValue> Fields);

/// <param name="Raw">Value exactly as read from the document.</param>
/// <param name="Normalized">Normalized value, or null when the field was not found.</param>
/// <param name="Confidence">Confidence between 0 and 1.</param>
/// <param name="ValidationStatus">VALID, INVALID, NOT_FOUND or UNCERTAIN.</param>
/// <param name="PageNumber">Page the evidence came from.</param>
/// <param name="BoundingBox">Evidence polygon as flat x/y pairs.</param>
/// <param name="ValidationMessages">
/// Machine readable codes that explain the status, such as CHECK_DIGIT_VALID or NO_LABEL_NEARBY.
/// Null means none.
/// </param>
public sealed record ExtractedFieldValue(
    string? Raw,
    string? Normalized,
    decimal? Confidence,
    string ValidationStatus,
    int? PageNumber,
    IReadOnlyList<decimal> BoundingBox,
    IReadOnlyList<string>? ValidationMessages = null);
