namespace DocReader.Application.Classification;

/// <summary>
/// Everything the classifier weighed for one text: the decision and, for every type tried, the score and
/// the evidence behind it. Meant for understanding why a real document came out UNKNOWN.
/// </summary>
/// <param name="ClassifierVersion">Version of the rules that ran.</param>
/// <param name="DocumentType">The decision, UNKNOWN when no type reached its threshold.</param>
/// <param name="Confidence">Score of the chosen type; null when UNKNOWN.</param>
/// <param name="Reason">The decision in a sentence.</param>
/// <param name="ThresholdOverride">Global minimum score in force, null when each type uses its own.</param>
/// <param name="TextLength">Letters and digits the matcher had to work with.</param>
/// <param name="Candidates">Every type tried, best score first.</param>
public sealed record ClassificationDiagnostics(
    string ClassifierVersion,
    string DocumentType,
    decimal? Confidence,
    string Reason,
    decimal? ThresholdOverride,
    int TextLength,
    IReadOnlyList<CandidateDiagnostics> Candidates);
