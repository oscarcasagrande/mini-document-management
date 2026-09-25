namespace DocReader.Application.Classification;

/// <summary>
/// One piece of evidence that a document is of some type: any of the alternative <paramref name="Patterns"/>
/// found in the text counts once, for <paramref name="Weight"/> points.
///
/// Patterns are written without accents and in upper case, as the text is normalized the same way. The
/// alternatives cover the wording variants of real documents (abbreviations, older layouts, bilingual
/// headers), so a variant is one more pattern and not one more evidence.
/// </summary>
/// <param name="Name">Stable identifier reported in the result and in the diagnostics.</param>
/// <param name="Patterns">Alternative wordings; the first one found wins.</param>
/// <param name="Weight">Points added when the evidence is found, or subtracted for counter-evidence.</param>
public sealed record ClassificationEvidence(string Name, IReadOnlyList<string> Patterns, decimal Weight);
