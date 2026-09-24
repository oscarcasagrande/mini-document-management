namespace DocReader.Application.Abstractions;

/// <summary>
/// OCR contract of PRD section 11. No implementation ships in stage 1: the ocr-service container
/// is a health check stub until stage 2 is approved.
/// </summary>
public interface IDocumentOcrProvider
{
    string ProviderName { get; }

    Task<OcrResult> AnalyzeAsync(DocumentContent document, OcrOptions options, CancellationToken ct);
}
