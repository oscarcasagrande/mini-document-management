namespace DocReader.Domain.Extractions;

/// <summary>
/// Result of one processing attempt that reached the end of the pipeline. Reprocessing adds a new
/// extraction and never overwrites the previous one (RF-013).
/// </summary>
public sealed class DocumentExtraction
{
    private readonly List<ExtractedField> _fields = [];

    private DocumentExtraction()
    {
    }

    public Guid Id { get; private init; }

    public Guid DocumentId { get; private init; }

    public Guid ProcessingJobId { get; private init; }

    public string OcrProvider { get; private init; } = string.Empty;

    /// <summary>Pipeline and library versions, recorded for reproducibility (RF-008).</summary>
    public string OcrModelVersion { get; private init; } = string.Empty;

    public string? ClassifierVersion { get; private init; }

    public string? ExtractorVersion { get; private init; }

    public int? SchemaVersion { get; private init; }

    /// <summary>Text of all pages, in page order. Never written to logs.</summary>
    public string RawText { get; private init; } = string.Empty;

    /// <summary>JSON array of <c>{ pageNumber, text }</c>, so the text endpoint can answer per page.</summary>
    public string PageTextsJson { get; private init; } = "[]";

    /// <summary>Untouched provider payload, preserved as required by RF-008.</summary>
    public string RawOcrResultJson { get; private init; } = "{}";

    /// <summary>Canonical structured result; null when the document type has no extractor.</summary>
    public string? StructuredResultJson { get; private init; }

    public decimal? OverallConfidence { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public IReadOnlyCollection<ExtractedField> Fields => _fields;

    public static DocumentExtraction Create(
        Guid documentId,
        Guid processingJobId,
        string ocrProvider,
        string ocrModelVersion,
        string? classifierVersion,
        string? extractorVersion,
        int? schemaVersion,
        string rawText,
        string pageTextsJson,
        string rawOcrResultJson,
        string? structuredResultJson,
        decimal? overallConfidence,
        DateTimeOffset createdAt,
        IEnumerable<ExtractedField> fields)
    {
        var extraction = new DocumentExtraction
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            ProcessingJobId = processingJobId,
            OcrProvider = ocrProvider,
            OcrModelVersion = ocrModelVersion,
            ClassifierVersion = classifierVersion,
            ExtractorVersion = extractorVersion,
            SchemaVersion = schemaVersion,
            RawText = rawText,
            PageTextsJson = pageTextsJson,
            RawOcrResultJson = rawOcrResultJson,
            StructuredResultJson = structuredResultJson,
            OverallConfidence = overallConfidence,
            CreatedAt = createdAt
        };

        foreach (var field in fields)
        {
            extraction._fields.Add(field.AttachTo(extraction.Id));
        }

        return extraction;
    }
}
