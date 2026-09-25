namespace DocReader.Application.Classification;

/// <summary>How a pattern was found in the text.</summary>
/// <param name="Pattern">The pattern that matched.</param>
/// <param name="Edits">0 for an exact match, otherwise the number of OCR errors tolerated.</param>
public sealed record TextMatch(string Pattern, int Edits)
{
    public string Kind => Edits == 0 ? "EXACT" : "FUZZY";
}
