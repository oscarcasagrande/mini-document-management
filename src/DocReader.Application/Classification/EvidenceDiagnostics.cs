namespace DocReader.Application.Classification;

/// <param name="Name">Evidence identifier, as in the profile.</param>
/// <param name="Weight">Points the evidence adds (or removes, for counter-evidence).</param>
/// <param name="Matched">Whether it was found in the text.</param>
/// <param name="MatchedPattern">Profile pattern that matched; a fixed string of the rules, never document content.</param>
/// <param name="MatchKind">EXACT, or FUZZY when OCR errors had to be tolerated; null when not found.</param>
/// <param name="Edits">Errors tolerated for a FUZZY match.</param>
public sealed record EvidenceDiagnostics(
    string Name,
    decimal Weight,
    bool Matched,
    string? MatchedPattern,
    string? MatchKind,
    int? Edits);
