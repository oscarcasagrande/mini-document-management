namespace DocReader.Application.Classification;

/// <param name="DocumentType">Type this profile recognises.</param>
/// <param name="Score">Points of evidence found minus counter-evidence, between 0 and 1.</param>
/// <param name="Threshold">Score the type needed to be accepted.</param>
/// <param name="Accepted">Whether the score reached the threshold.</param>
/// <param name="Reason">Why the score is what it is, in a sentence.</param>
/// <param name="Evidence">Every evidence of the profile, found or not.</param>
/// <param name="CounterEvidence">Every counter-evidence of the profile, found or not.</param>
public sealed record CandidateDiagnostics(
    string DocumentType,
    decimal Score,
    decimal Threshold,
    bool Accepted,
    string Reason,
    IReadOnlyList<EvidenceDiagnostics> Evidence,
    IReadOnlyList<EvidenceDiagnostics> CounterEvidence);
