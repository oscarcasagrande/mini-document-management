namespace DocReader.Application.Abstractions;

/// <summary>
/// Knobs handed to an OCR provider.
/// </summary>
/// <param name="Languages">Language hints, Portuguese first in the PoC.</param>
/// <param name="DetectLayout">Whether the provider should return layout structures.</param>
/// <param name="Timeout">Wall clock budget of one page, not of the whole document.</param>
/// <param name="CorrelationId">Propagated to the OCR service so one id follows the document end to end.</param>
public sealed record OcrOptions(
    IReadOnlyList<string> Languages,
    bool DetectLayout,
    TimeSpan Timeout,
    string? CorrelationId = null);
