using DocReader.Domain.Documents;

namespace DocReader.Api.Contracts.V1;

/// <summary>
/// One row of the listing.
/// </summary>
/// <param name="Id">Identity of the document.</param>
/// <param name="Protocol">Human readable protocol.</param>
/// <param name="FileName">Original file name as sent by the client.</param>
/// <param name="MimeType">MIME type detected from the file signature.</param>
/// <param name="SizeBytes">Size in bytes.</param>
/// <param name="PageCount">Number of pages.</param>
/// <param name="Channel">WEB when it came from the interface, API otherwise.</param>
/// <param name="Status">Current status.</param>
/// <param name="ExpectedDocumentType">Type hint sent at upload time, when any.</param>
/// <param name="DetectedDocumentType">Type identified by the classifier, null until the document is classified.</param>
/// <param name="ClassificationConfidence">Confidence between 0 and 1, when available.</param>
/// <param name="ExternalReference">Caller reference sent at upload time, when any.</param>
/// <param name="UploadedAt">Upload instant in UTC.</param>
/// <param name="CompletedAt">Instant processing finished, in UTC.</param>
/// <param name="Links">Related endpoints.</param>
public sealed record DocumentSummaryResponse(
    Guid Id,
    string Protocol,
    string FileName,
    string MimeType,
    long SizeBytes,
    int PageCount,
    UploadChannel Channel,
    DocumentStatus Status,
    string? ExpectedDocumentType,
    string? DetectedDocumentType,
    decimal? ClassificationConfidence,
    string? ExternalReference,
    DateTimeOffset UploadedAt,
    DateTimeOffset? CompletedAt,
    DocumentLinks Links);
