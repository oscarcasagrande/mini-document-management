using DocReader.Domain.Documents;

namespace DocReader.Api.Contracts.V1;

/// <summary>
/// Lightweight payload for polling, cheap enough to be called every couple of seconds.
/// </summary>
/// <param name="Id">Identity of the document.</param>
/// <param name="Protocol">Human readable protocol.</param>
/// <param name="Status">Current status.</param>
/// <param name="DetectedDocumentType">Type identified by the classifier, when available.</param>
/// <param name="ClassificationConfidence">Confidence between 0 and 1, when available.</param>
/// <param name="UploadedAt">Upload instant in UTC.</param>
/// <param name="CompletedAt">Instant processing finished, in UTC.</param>
/// <param name="LastError">Last processing error, when any.</param>
public sealed record DocumentStatusResponse(
    Guid Id,
    string Protocol,
    DocumentStatus Status,
    string? DetectedDocumentType,
    decimal? ClassificationConfidence,
    DateTimeOffset UploadedAt,
    DateTimeOffset? CompletedAt,
    DocumentErrorResponse? LastError);
