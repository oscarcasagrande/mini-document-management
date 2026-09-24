using DocReader.Domain.Documents;

namespace DocReader.Api.Contracts.V1;

/// <summary>
/// The raw text the OCR read, page by page, in reading order. Nothing was interpreted or corrected.
/// </summary>
/// <param name="Id">Identity of the document.</param>
/// <param name="Protocol">Human readable protocol.</param>
/// <param name="Status">Current status of the document. The text belongs to the latest extraction, even while a reprocessing runs.</param>
/// <param name="OcrProvider">Provider that produced the text.</param>
/// <param name="OcrModelVersion">Pipeline and library versions.</param>
/// <param name="ExtractedAt">Instant the text was persisted, in UTC.</param>
/// <param name="Pages">One entry per page.</param>
public sealed record DocumentTextResponse(
    Guid Id,
    string Protocol,
    DocumentStatus Status,
    string OcrProvider,
    string OcrModelVersion,
    DateTimeOffset ExtractedAt,
    IReadOnlyList<DocumentTextPageResponse> Pages);

/// <param name="Page">One based page number.</param>
/// <param name="Text">Text of the page, one recognized line per line.</param>
public sealed record DocumentTextPageResponse(int Page, string Text);
