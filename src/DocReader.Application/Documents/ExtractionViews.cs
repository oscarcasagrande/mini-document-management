using DocReader.Application.Classification;
using DocReader.Application.Extraction;
using DocReader.Domain.Documents;
using DocReader.Domain.Processing;

namespace DocReader.Application.Documents;

/// <summary>
/// Read models of the result. The raw OCR payload is stored for RF-008 but never loaded by the
/// ordinary queries, which would otherwise drag megabytes of polygons into every detail request.
/// </summary>
/// <param name="ExtractionId">Identity of the extraction.</param>
/// <param name="OcrProvider">Provider that produced the text.</param>
/// <param name="OcrModelVersion">Pipeline and library versions.</param>
/// <param name="ClassifierVersion">Classifier that ran, when any.</param>
/// <param name="ExtractorVersion">Extractor that ran, when any.</param>
/// <param name="SchemaVersion">Schema of the structured result, when there is one.</param>
/// <param name="OverallConfidence">Aggregated field confidence between 0 and 1.</param>
/// <param name="CreatedAt">Instant the extraction was persisted, in UTC.</param>
public sealed record ExtractionSummary(
    Guid ExtractionId,
    string OcrProvider,
    string OcrModelVersion,
    string? ClassifierVersion,
    string? ExtractorVersion,
    int? SchemaVersion,
    decimal? OverallConfidence,
    DateTimeOffset CreatedAt);

/// <param name="FieldPath">Path in the schema of the document type, such as <c>cpf</c>.</param>
/// <param name="RawValue">Value exactly as read.</param>
/// <param name="NormalizedValue">Normalized value, null when not found.</param>
/// <param name="Confidence">Confidence between 0 and 1.</param>
/// <param name="PageNumber">Page of the evidence.</param>
/// <param name="BoundingBoxJson">JSON array of flat x/y pairs.</param>
/// <param name="ValidationStatus">VALID, INVALID, NOT_FOUND or UNCERTAIN.</param>
/// <param name="ValidationMessagesJson">JSON array of machine readable codes.</param>
public sealed record ExtractedFieldView(
    string FieldPath,
    string? RawValue,
    string? NormalizedValue,
    decimal? Confidence,
    int? PageNumber,
    string BoundingBoxJson,
    string ValidationStatus,
    string ValidationMessagesJson);

public sealed record ExtractionResultView(
    ExtractionSummary Summary,
    IReadOnlyList<ExtractedFieldView> Fields);

/// <param name="Summary">Identity and versions of the extraction the text belongs to.</param>
/// <param name="PageTextsJson">JSON array of <c>{ pageNumber, text }</c>.</param>
public sealed record ExtractionTextView(ExtractionSummary Summary, string PageTextsJson);

/// <param name="Summary">Identity and versions of the extraction.</param>
/// <param name="RawOcrResultJson">Stored OCR payload: per page, the provider output and the normalized blocks.</param>
/// <param name="PageTextsJson">JSON array of <c>{ pageNumber, text }</c>.</param>
public sealed record ExtractionOcrView(ExtractionSummary Summary, string RawOcrResultJson, string PageTextsJson);

/// <summary>Document with the latest state of its asynchronous work, for the status and detail views.</summary>
public sealed record DocumentSnapshot(
    Document Document,
    ProcessingJob? LatestJob,
    ExtractionSummary? LatestExtraction);

public sealed record DocumentResult(Document Document, ExtractionResultView Result);

public sealed record DocumentText(Document Document, ExtractionTextView Text);

public enum ReprocessOutcome
{
    Queued = 0,
    NotFound = 1,

    /// <summary>A job is already pending or running, or the document is in a status that cannot be reprocessed.</summary>
    Conflict = 2
}

/// <param name="PageNumber">One based page number.</param>
/// <param name="Text">Text of the page, one recognized line per line.</param>
public sealed record DocumentTextPage(int PageNumber, string Text);

/// <summary>
/// The classifier run again, with today's rules, over the text of the latest extraction, next to what was
/// recorded when the document was processed. They differ after the rules changed and before a reprocess.
/// </summary>
/// <summary>
/// The extraction run again over the stored OCR of the latest extraction, with the rules in force now, and what it
/// tried for each field. The recorded field statuses travel inside so a stale result is visible.
/// </summary>
public sealed record DocumentExtractionDiagnostics(
    Document Document,
    ExtractionSummary Extraction,
    ExtractionDiagnostics Diagnostics);

public sealed record DocumentClassificationDiagnostics(
    Document Document,
    ExtractionSummary Extraction,
    IReadOnlyList<DocumentTextPage> Pages,
    ClassificationDiagnostics Diagnostics);
