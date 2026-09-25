namespace DocReader.Application.Abstractions;

/// <summary>
/// OCR contract of PRD section 11. The PoC implementation talks to the internal ocr-service over
/// HTTP, one page per call.
/// </summary>
public interface IDocumentOcrProvider
{
    string ProviderName { get; }

    /// <summary>
    /// Reads every page of the document. <paramref name="progress"/> is awaited after each page, which
    /// is where the worker renews its lock; a provider must not start the next page before it returns.
    /// </summary>
    /// <exception cref="Errors.OcrProviderException">The provider failed; the exception says whether a retry can help.</exception>
    Task<OcrResult> AnalyzeAsync(
        DocumentContent document,
        OcrOptions options,
        IOcrProgress? progress,
        CancellationToken ct);
}
