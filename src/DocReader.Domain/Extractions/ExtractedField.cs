namespace DocReader.Domain.Extractions;

/// <summary>
/// One field read from a document, with the evidence it came from.
/// </summary>
public sealed class ExtractedField
{
    private ExtractedField()
    {
    }

    public Guid Id { get; private init; }

    public Guid ExtractionId { get; private set; }

    /// <summary>Path of the field in the schema of the document type, such as <c>cpf</c>.</summary>
    public string FieldPath { get; private init; } = string.Empty;

    /// <summary>Value exactly as read from the document.</summary>
    public string? RawValue { get; private init; }

    public string? NormalizedValue { get; private init; }

    public decimal? Confidence { get; private init; }

    public int? PageNumber { get; private init; }

    /// <summary>JSON array of flat x/y pairs of the evidence polygon.</summary>
    public string BoundingBoxJson { get; private init; } = "[]";

    /// <summary>VALID, INVALID, NOT_FOUND or UNCERTAIN.</summary>
    public string ValidationStatus { get; private init; } = string.Empty;

    /// <summary>JSON array of machine readable validation codes, such as <c>CHECK_DIGIT_VALID</c>.</summary>
    public string ValidationMessagesJson { get; private init; } = "[]";

    public static ExtractedField Create(
        string fieldPath,
        string? rawValue,
        string? normalizedValue,
        decimal? confidence,
        int? pageNumber,
        string boundingBoxJson,
        string validationStatus,
        string validationMessagesJson) =>
        new()
        {
            Id = Guid.NewGuid(),
            FieldPath = fieldPath,
            RawValue = rawValue,
            NormalizedValue = normalizedValue,
            Confidence = confidence,
            PageNumber = pageNumber,
            BoundingBoxJson = boundingBoxJson,
            ValidationStatus = validationStatus,
            ValidationMessagesJson = validationMessagesJson
        };

    internal ExtractedField AttachTo(Guid extractionId)
    {
        ExtractionId = extractionId;
        return this;
    }
}
