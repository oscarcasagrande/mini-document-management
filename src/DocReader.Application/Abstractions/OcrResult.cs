namespace DocReader.Application.Abstractions;

/// <summary>
/// Normalized OCR output. Reserved for stage 2 of the execution plan.
/// </summary>
/// <param name="ProviderName">Provider that produced the result.</param>
/// <param name="ModelVersion">Model or pipeline version, recorded for reproducibility.</param>
/// <param name="Pages">Per page text and blocks.</param>
/// <param name="RawResult">Untouched provider payload, preserved as required by RF-008.</param>
public sealed record OcrResult(
    string ProviderName,
    string ModelVersion,
    IReadOnlyList<OcrPage> Pages,
    string RawResult);

/// <param name="PageNumber">One based page number.</param>
/// <param name="Text">Concatenated text of the page.</param>
/// <param name="Blocks">Blocks with coordinates, when the provider supplies them.</param>
public sealed record OcrPage(int PageNumber, string Text, IReadOnlyList<OcrBlock> Blocks);

/// <param name="Text">Block text.</param>
/// <param name="Confidence">Provider confidence between 0 and 1.</param>
/// <param name="BoundingBox">Polygon as flat x/y pairs, empty when unavailable.</param>
public sealed record OcrBlock(string Text, decimal? Confidence, IReadOnlyList<decimal> BoundingBox);
