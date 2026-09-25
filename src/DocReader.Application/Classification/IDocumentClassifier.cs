using DocReader.Application.Abstractions;

namespace DocReader.Application.Classification;

/// <summary>
/// Classifier of RF-010: decides the document type from the OCR text, with the signals that
/// justified the decision.
/// </summary>
public interface IDocumentClassifier
{
    string Version { get; }

    ClassificationResult Classify(OcrResult result);

    /// <summary>
    /// Runs the same decision on plain text and reports what was weighed for every type, so an UNKNOWN
    /// can be explained without reading the rules.
    /// </summary>
    ClassificationDiagnostics Diagnose(string text);
}

/// <param name="DocumentType">Identified type, or <see cref="UnknownType"/> without enough evidence.</param>
/// <param name="Confidence">Score between 0 and 1; null when the type is unknown.</param>
/// <param name="Signals">Signals found in the text, so the decision can be audited.</param>
/// <param name="ClassifierVersion">Version recorded with the extraction (RF-013).</param>
public sealed record ClassificationResult(
    string DocumentType,
    decimal? Confidence,
    IReadOnlyList<string> Signals,
    string ClassifierVersion)
{
    public const string UnknownType = "UNKNOWN";

    public bool IsKnown => !string.Equals(DocumentType, UnknownType, StringComparison.Ordinal);
}
