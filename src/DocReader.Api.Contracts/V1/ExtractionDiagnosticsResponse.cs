using DocReader.Domain.Documents;

namespace DocReader.Api.Contracts.V1;

/// <summary>
/// Why the fields of a document came out as they did: the OCR blocks with coordinates as the extractor received
/// them, and for every field what the extraction rules in force now read, the reason it failed and what its rule
/// searched. Compare <c>status</c> with <c>recordedStatus</c> to see a result made stale by a rule change.
/// </summary>
/// <param name="Id">Identity of the document.</param>
/// <param name="Protocol">Human readable protocol.</param>
/// <param name="Status">Current status of the document.</param>
/// <param name="ExtractedAt">Instant the OCR of the latest extraction was persisted, in UTC.</param>
/// <param name="RecordedExtractorVersion">Extractor version stored with the latest extraction.</param>
/// <param name="DocumentType">Type whose extractor ran; null when the document is UNKNOWN.</param>
/// <param name="DocumentTypeSource">RECORDED, or CURRENT_CLASSIFICATION when the recorded type was UNKNOWN and today's rules recognise one.</param>
/// <param name="ExtractorVersion">Extractor version that ran now.</param>
/// <param name="OcrInput">BLOCKS (with coordinates) or PAGE_TEXT (an older extraction: plain lines, no geometry).</param>
/// <param name="Note">A warning about this diagnosis, when there is one.</param>
/// <param name="Coverage">How many fields were read.</param>
/// <param name="Fields">One entry per field of the type.</param>
/// <param name="Pages">The OCR blocks with coordinates, page by page. Omitted with <c>includeText=false</c>.</param>
public sealed record ExtractionDiagnosticsResponse(
    Guid Id,
    string Protocol,
    DocumentStatus Status,
    DateTimeOffset ExtractedAt,
    string? RecordedExtractorVersion,
    string? DocumentType,
    string DocumentTypeSource,
    string? ExtractorVersion,
    string OcrInput,
    string? Note,
    ExtractionCoverageResponse Coverage,
    IReadOnlyList<DiagnosedFieldResponse> Fields,
    IReadOnlyList<DiagnosedPageResponse>? Pages);

/// <param name="Expected">Fields of the type, not counting dynamic ones such as contract partners.</param>
/// <param name="Extracted">Fields read, valid or not: any status other than NOT_FOUND.</param>
/// <param name="Valid">Fields read and valid.</param>
/// <param name="ExtractedRatio">Extracted over expected, between 0 and 1.</param>
/// <param name="ValidRatio">Valid over expected, between 0 and 1.</param>
public sealed record ExtractionCoverageResponse(int Expected, int Extracted, int Valid, decimal ExtractedRatio, decimal ValidRatio);

/// <param name="Path">Field path in the schema of the type.</param>
/// <param name="Status">VALID, INVALID, NOT_FOUND or UNCERTAIN, with the rules in force now.</param>
/// <param name="Reason">FOUND, FOUND_WITHOUT_LABEL, LABEL_NOT_FOUND, VALUE_EMPTY, VALUE_REJECTED, VALIDATION_FAILED, VALUE_UNCERTAIN or PATTERN_NOT_FOUND.</param>
/// <param name="Explanation">The reason in a sentence.</param>
/// <param name="Messages">Machine readable validation codes.</param>
/// <param name="Raw">Value as read. Omitted with <c>includeText=false</c>.</param>
/// <param name="Normalized">Normalized value. Omitted with <c>includeText=false</c>.</param>
/// <param name="Confidence">Confidence between 0 and 1.</param>
/// <param name="Page">Page of the evidence.</param>
/// <param name="BoundingBox">Flat x/y pairs, in pixels of the analysed page image.</param>
/// <param name="RecordedStatus">Status stored when the document was processed; differs from <c>status</c> after a rule change, until reprocessing.</param>
/// <param name="Rule">What the field rule searched; null for a field that is not read by label.</param>
public sealed record DiagnosedFieldResponse(
    string Path,
    string Status,
    string Reason,
    string Explanation,
    IReadOnlyList<string> Messages,
    string? Raw,
    string? Normalized,
    decimal? Confidence,
    int? Page,
    IReadOnlyList<decimal> BoundingBox,
    string? RecordedStatus,
    DiagnosedRuleResponse? Rule);

/// <param name="Labels">Labels searched, each with the lines it was found on (empty when it was not found).</param>
/// <param name="CandidatesSeen">How many candidate values the rule examined.</param>
/// <param name="Candidates">The first candidates examined.</param>
public sealed record DiagnosedRuleResponse(
    IReadOnlyList<DiagnosedLabelResponse> Labels,
    int CandidatesSeen,
    IReadOnlyList<DiagnosedCandidateResponse> Candidates);

/// <param name="Label">Label searched, normalized without accents and in upper case.</param>
/// <param name="FoundOnLines">Block indexes (see <c>pages</c>) where it appeared.</param>
public sealed record DiagnosedLabelResponse(string Label, IReadOnlyList<int> FoundOnLines);

/// <param name="LineIndex">Block index the candidate came from.</param>
/// <param name="Page">Page of the block.</param>
/// <param name="Text">Candidate text. Omitted with <c>includeText=false</c>.</param>
/// <param name="Penalty">Confidence discount for the distance from the label.</param>
/// <param name="Accepted">Whether the value read came from this candidate.</param>
public sealed record DiagnosedCandidateResponse(int LineIndex, int Page, string? Text, decimal Penalty, bool Accepted);

/// <param name="Page">One based page number.</param>
/// <param name="Blocks">Blocks of the page, in reading order.</param>
public sealed record DiagnosedPageResponse(int Page, IReadOnlyList<DiagnosedBlockResponse> Blocks);

/// <param name="Index">Reading-order position; the one labels and candidates refer to.</param>
/// <param name="Text">Recognised text.</param>
/// <param name="Confidence">OCR confidence between 0 and 1.</param>
/// <param name="BoundingBox">Polygon as flat x/y pairs, in pixels of the analysed page image.</param>
public sealed record DiagnosedBlockResponse(int Index, string Text, decimal? Confidence, IReadOnlyList<decimal> BoundingBox);
