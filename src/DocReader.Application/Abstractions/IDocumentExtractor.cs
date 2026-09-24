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
}
