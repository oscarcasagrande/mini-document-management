namespace DocReader.Application.Abstractions;

/// <summary>
/// Knobs handed to an OCR provider. Reserved for stage 2 of the execution plan.
/// </summary>
/// <param name="Languages">Language hints, Portuguese first in the PoC.</param>
/// <param name="DetectLayout">Whether the provider should return layout structures.</param>
/// <param name="Timeout">Wall clock budget for the call.</param>
public sealed record OcrOptions(IReadOnlyList<string> Languages, bool DetectLayout, TimeSpan Timeout);
