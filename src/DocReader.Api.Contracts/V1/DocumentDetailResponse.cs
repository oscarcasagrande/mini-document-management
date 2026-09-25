using DocReader.Domain.Documents;

namespace DocReader.Api.Contracts.V1;

/// <summary>
/// Consolidated view of a document. Classification and extraction stay null until the worker has
/// produced them; the original file is always available regardless.
/// </summary>
/// <param name="Id">Identity of the document.</param>
/// <param name="Protocol">Human readable protocol.</param>
/// <param name="Status">Current status.</param>
/// <param name="Upload">What was received.</param>
/// <param name="Classification">Identified type, when the classifier already ran.</param>
/// <param name="Extraction">Identity of the latest result, when the pipeline already produced one.</param>
/// <param name="LastError">Last processing error, when any.</param>
/// <param name="Processing">Progress of the asynchronous work, null when no job exists.</param>
/// <param name="Timeline">Ordered processing events.</param>
/// <param name="Links">Related endpoints.</param>
public sealed record DocumentDetailResponse(
    Guid Id,
    string Protocol,
    DocumentStatus Status,
    DocumentUploadResponse Upload,
    DocumentClassificationResponse? Classification,
    DocumentExtractionResponse? Extraction,
    DocumentErrorResponse? LastError,
    DocumentProcessingResponse? Processing,
    IReadOnlyList<DocumentTimelineEntryResponse> Timeline,
    DocumentLinks Links);

/// <param name="FileName">Original file name as sent by the client.</param>
/// <param name="Channel">WEB when it came from the interface, API otherwise.</param>
/// <param name="UploadedAt">Upload instant in UTC.</param>
/// <param name="CompletedAt">Instant processing finished, in UTC.</param>
/// <param name="ExternalReference">Caller reference sent at upload time.</param>
/// <param name="ExpectedDocumentType">Type hint sent at upload time.</param>
/// <param name="MimeType">MIME type detected from the file signature.</param>
/// <param name="SizeBytes">Size in bytes.</param>
/// <param name="PageCount">Number of pages.</param>
/// <param name="Sha256">SHA-256 of the stored bytes.</param>
public sealed record DocumentUploadResponse(
    string FileName,
    UploadChannel Channel,
    DateTimeOffset UploadedAt,
    DateTimeOffset? CompletedAt,
    string? ExternalReference,
    string? ExpectedDocumentType,
    string MimeType,
    long SizeBytes,
    int PageCount,
    string Sha256);

/// <param name="DetectedType">Identified document type, or UNKNOWN.</param>
/// <param name="Confidence">Confidence between 0 and 1.</param>
public sealed record DocumentClassificationResponse(string DetectedType, decimal? Confidence);

/// <param name="OcrProvider">Provider that produced the text.</param>
/// <param name="OcrModelVersion">Pipeline and library versions, for reproducibility.</param>
/// <param name="SchemaVersion">Version of the schema used for the fields; null when the type has no extractor.</param>
/// <param name="OverallConfidence">Aggregated confidence between 0 and 1.</param>
/// <param name="ExtractedAt">Instant the result was persisted, in UTC.</param>
public sealed record DocumentExtractionResponse(
    string OcrProvider,
    string OcrModelVersion,
    int? SchemaVersion,
    decimal? OverallConfidence,
    DateTimeOffset ExtractedAt);

/// <param name="Code">Stable machine readable code.</param>
/// <param name="Message">Short operator facing description, never document content.</param>
public sealed record DocumentErrorResponse(string Code, string Message);

/// <param name="EventType">Event name, for example RECEIVED or QUEUED.</param>
/// <param name="Stage">Status the document was in.</param>
/// <param name="Details">Extra context, never document content.</param>
/// <param name="OccurredAt">Instant in UTC.</param>
public sealed record DocumentTimelineEntryResponse(
    string EventType,
    DocumentStatus Stage,
    string? Details,
    DateTimeOffset OccurredAt);
