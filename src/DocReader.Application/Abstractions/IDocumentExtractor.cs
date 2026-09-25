namespace DocReader.Application.Abstractions;

/// <summary>
/// Extractor contract of PRD section 11, one implementation per document type.
/// </summary>
public interface IDocumentExtractor
{
    string DocumentType { get; }

    int SchemaVersion { get; }

    /// <summary>Version of the extraction rules, recorded with each extraction (RF-013).</summary>
    string Version { get; }

    Task<StructuredExtraction> ExtractAsync(OcrResult result, CancellationToken ct);

    /// <summary>
    /// Como <see cref="ExtractAsync(OcrResult, CancellationToken)"/>, registrando em <paramref name="trace"/> os rótulos
    /// procurados e os candidatos vistos por campo. Com <c>null</c> é a extração normal.
    /// </summary>
    Task<StructuredExtraction> ExtractAsync(OcrResult result, Extraction.ExtractionTrace? trace, CancellationToken ct);
}
