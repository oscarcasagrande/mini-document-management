namespace DocReader.Application.Abstractions;

/// <summary>
/// Normalized OCR output.
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
/// <param name="HasNativeTextLayer">The page's text came from a PDF's own text layer (pdfplumber), not OCR (RF-009).</param>
/// <param name="RotationDegrees">Cardinal rotation the service corrected before reading the page: 0, 90, 180 or 270.</param>
/// <param name="Deskewed">Whether a small tilt was also straightened, on top of any cardinal rotation.</param>
/// <param name="ProcessedWithStructure">Whether PP-StructureV3 replaced PP-OCRv5's reading of this page (a suspected table, low confidence, opt-in).</param>
public sealed record OcrPage(
    int PageNumber,
    string Text,
    IReadOnlyList<OcrBlock> Blocks,
    bool HasNativeTextLayer = false,
    int RotationDegrees = 0,
    bool Deskewed = false,
    bool ProcessedWithStructure = false);

/// <param name="Text">Block text.</param>
/// <param name="Confidence">Provider confidence between 0 and 1.</param>
/// <param name="BoundingBox">Polygon as flat x/y pairs, empty when unavailable.</param>
public sealed record OcrBlock(string Text, decimal? Confidence, IReadOnlyList<decimal> BoundingBox);
