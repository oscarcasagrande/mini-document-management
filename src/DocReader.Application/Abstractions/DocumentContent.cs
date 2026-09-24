namespace DocReader.Application.Abstractions;

/// <summary>
/// Input handed to an OCR provider.
/// </summary>
/// <param name="DocumentId">Document being analysed.</param>
/// <param name="MimeType">Detected MIME type of the original file.</param>
/// <param name="PageCount">Number of pages of the original file.</param>
/// <param name="Content">Seekable stream of the original bytes.</param>
public sealed record DocumentContent(Guid DocumentId, string MimeType, int PageCount, Stream Content);
