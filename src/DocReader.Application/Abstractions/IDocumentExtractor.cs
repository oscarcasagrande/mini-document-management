namespace DocReader.Application.Abstractions;

/// <summary>
/// Extractor contract of PRD section 11, one implementation per document type. Reserved for
/// stage 3 of the execution plan.
/// </summary>
public interface IDocumentExtractor
{
    string DocumentType { get; }

    int SchemaVersion { get; }

    Task<StructuredExtraction> ExtractAsync(OcrResult result, CancellationToken ct);
}
