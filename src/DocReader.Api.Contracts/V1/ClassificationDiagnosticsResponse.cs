using DocReader.Domain.Documents;

namespace DocReader.Api.Contracts.V1;

/// <summary>
/// Why a document was classified as it was: the classification recorded at processing time, the same
/// decision recomputed with the rules in force now, every document type that was tried with its score and
/// evidence, and the raw text of the latest extraction.
/// </summary>
/// <param name="Id">Identity of the document.</param>
/// <param name="Protocol">Human readable protocol.</param>
/// <param name="Status">Current status of the document.</param>
/// <param name="ExtractedAt">Instant the text was persisted, in UTC.</param>
/// <param name="Recorded">What was stored when the document was last processed.</param>
/// <param name="Current">The decision of the rules in force now. It differs from <c>recorded</c> after the rules changed, until the document is reprocessed.</param>
/// <param name="Candidates">Every document type tried, best score first.</param>
/// <param name="Pages">Raw OCR text, page by page. Omitted with <c>includeText=false</c>.</param>
public sealed record ClassificationDiagnosticsResponse(
    Guid Id,
    string Protocol,
    DocumentStatus Status,
    DateTimeOffset ExtractedAt,
    RecordedClassificationResponse Recorded,
    ClassificationDecisionResponse Current,
    IReadOnlyList<ClassificationCandidateResponse> Candidates,
    IReadOnlyList<DocumentTextPageResponse>? Pages);

/// <param name="DetectedType">Type stored with the document; UNKNOWN or null when none was found.</param>
/// <param name="Confidence">Score stored with the document.</param>
/// <param name="ClassifierVersion">Rules version of the latest extraction.</param>
public sealed record RecordedClassificationResponse(string? DetectedType, decimal? Confidence, string? ClassifierVersion);

/// <param name="ClassifierVersion">Version of the rules that ran now.</param>
/// <param name="DocumentType">The decision, UNKNOWN when no type reached its threshold.</param>
/// <param name="Confidence">Score of the chosen type; null when UNKNOWN.</param>
/// <param name="Reason">The decision in a sentence.</param>
/// <param name="MinimumScoreOverride">Global minimum score in force, null when each type uses its own threshold.</param>
/// <param name="TextLength">Letters and digits the matcher worked with.</param>
public sealed record ClassificationDecisionResponse(
    string ClassifierVersion,
    string DocumentType,
    decimal? Confidence,
    string Reason,
    decimal? MinimumScoreOverride,
    int TextLength);

/// <param name="DocumentType">Type this candidate would classify the document as.</param>
/// <param name="Score">Evidence found minus counter-evidence, between 0 and 1.</param>
/// <param name="Threshold">Score needed to accept the type.</param>
/// <param name="Accepted">Whether the score reached the threshold.</param>
/// <param name="Reason">Why the score is what it is.</param>
/// <param name="Evidence">Every evidence of the type, found or not.</param>
/// <param name="CounterEvidence">Every counter-evidence of the type, found or not.</param>
public sealed record ClassificationCandidateResponse(
    string DocumentType,
    decimal Score,
    decimal Threshold,
    bool Accepted,
    string Reason,
    IReadOnlyList<ClassificationEvidenceResponse> Evidence,
    IReadOnlyList<ClassificationEvidenceResponse> CounterEvidence);

/// <param name="Name">Evidence identifier.</param>
/// <param name="Weight">Points added, or removed for counter-evidence.</param>
/// <param name="Matched">Whether it was found in the text.</param>
/// <param name="MatchedPattern">Rule pattern that matched; not document content.</param>
/// <param name="MatchKind">EXACT, or FUZZY when OCR errors were tolerated.</param>
/// <param name="Edits">Errors tolerated for a FUZZY match.</param>
public sealed record ClassificationEvidenceResponse(
    string Name,
    decimal Weight,
    bool Matched,
    string? MatchedPattern,
    string? MatchKind,
    int? Edits);
