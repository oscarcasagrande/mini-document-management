using DocReader.Domain.Documents;

namespace DocReader.Api.Contracts.V1;

/// <summary>
/// Canonical result of PRD section 15: what was received, what the document was identified as and
/// the fields read from it, each with its raw and normalized value, confidence, validation status
/// and evidence.
/// </summary>
/// <param name="Id">Identity of the document.</param>
/// <param name="Protocol">Human readable protocol.</param>
/// <param name="Status">Current status of the document. The result belongs to the latest extraction, even while a reprocessing runs.</param>
/// <param name="Upload">What was received.</param>
/// <param name="Classification">Identified type, or UNKNOWN when the evidence was not enough.</param>
/// <param name="Extraction">The extraction itself.</param>
public sealed record DocumentResultResponse(
    Guid Id,
    string Protocol,
    DocumentStatus Status,
    DocumentResultUploadResponse Upload,
    DocumentResultClassificationResponse Classification,
    DocumentResultExtractionResponse Extraction);

/// <param name="FileName">Original file name as sent by the client.</param>
/// <param name="Channel">WEB when it came from the interface, API otherwise.</param>
/// <param name="UploadedAt">Upload instant in UTC.</param>
/// <param name="ExternalReference">Caller reference sent at upload time.</param>
public sealed record DocumentResultUploadResponse(
    string FileName,
    UploadChannel Channel,
    DateTimeOffset UploadedAt,
    string? ExternalReference);

/// <param name="DetectedType">Identified document type, or UNKNOWN.</param>
/// <param name="Confidence">Confidence between 0 and 1; null when UNKNOWN.</param>
/// <param name="ClassifierVersion">Classifier that decided.</param>
public sealed record DocumentResultClassificationResponse(
    string DetectedType,
    decimal? Confidence,
    string? ClassifierVersion);

/// <param name="OcrProvider">Provider that produced the text.</param>
/// <param name="OcrModelVersion">Pipeline and library versions.</param>
/// <param name="ExtractorVersion">Version of the extraction rules; null when the type has no extractor.</param>
/// <param name="SchemaVersion">Version of the schema of the fields; null when the type has no extractor.</param>
/// <param name="OverallConfidence">Aggregated confidence between 0 and 1.</param>
/// <param name="ExtractedAt">Instant the result was persisted, in UTC.</param>
/// <param name="Fields">Fields by name; empty when the document type has no extractor.</param>
public sealed record DocumentResultExtractionResponse(
    string OcrProvider,
    string OcrModelVersion,
    string? ExtractorVersion,
    int? SchemaVersion,
    decimal? OverallConfidence,
    DateTimeOffset ExtractedAt,
    IReadOnlyDictionary<string, ExtractedFieldResponse> Fields);

/// <param name="Raw">Value exactly as read from the document.</param>
/// <param name="Normalized">Normalized value; null when the field was not found or did not validate.</param>
/// <param name="Confidence">Confidence between 0 and 1.</param>
/// <param name="ValidationStatus">VALID, INVALID, NOT_FOUND or UNCERTAIN.</param>
/// <param name="ValidationMessages">Machine readable codes that explain the status, such as CHECK_DIGIT_VALID.</param>
/// <param name="Evidence">Where in the document the value came from.</param>
public sealed record ExtractedFieldResponse(
    string? Raw,
    string? Normalized,
    decimal? Confidence,
    string ValidationStatus,
    IReadOnlyList<string> ValidationMessages,
    FieldEvidenceResponse Evidence);

/// <param name="Page">One based page of the evidence; null when the field was not found.</param>
/// <param name="BoundingBox">Flat x/y pairs of the text polygon, in pixels of the analysed page image.</param>
public sealed record FieldEvidenceResponse(int? Page, IReadOnlyList<decimal> BoundingBox);
