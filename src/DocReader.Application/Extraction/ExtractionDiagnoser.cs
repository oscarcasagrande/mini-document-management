using DocReader.Application.Abstractions;
using DocReader.Domain;
using DocReader.Domain.Validation;

namespace DocReader.Application.Extraction;

/// <summary>
/// Junta o resultado de uma extração com o rastro que ela deixou e deduz, por campo, por que ele está como está.
/// A dedução usa só o que o rastro registra: rótulo achado ou não, candidatos vistos ou não, status devolvido.
/// </summary>
internal static class ExtractionDiagnoser
{
    private const string NotFound = "NOT_FOUND";

    /// <summary>Prefixo dos campos dinâmicos (sócios), que não entram na conta de cobertura.</summary>
    private const string DynamicPrefix = "partners[";

    public static ExtractionDiagnostics Build(
        string documentType,
        string documentTypeSource,
        string extractorVersion,
        string ocrInput,
        IReadOnlyList<OcrTextLine> lines,
        StructuredExtraction extraction,
        ExtractionTrace trace,
        IReadOnlyDictionary<string, string> recordedStatuses,
        string? note)
    {
        var fields = extraction.Fields
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => Field(pair.Key, pair.Value, trace.ScopeOf(pair.Key), recordedStatuses))
            .ToArray();

        var counted = fields.Where(field => !field.Path.StartsWith(DynamicPrefix, StringComparison.Ordinal)).ToArray();

        return new ExtractionDiagnostics(
            documentType,
            documentTypeSource,
            extractorVersion,
            ocrInput,
            Pages(lines),
            fields,
            new ExtractionCoverage(
                counted.Length,
                counted.Count(field => field.Status != NotFound),
                counted.Count(field => field.Status == EnumNaming.ToUpperSnakeCase(FieldValidationStatus.Valid))),
            note);
    }

    public static ExtractionDiagnostics WithoutExtractor(
        string? documentType,
        string source,
        string ocrInput,
        IReadOnlyList<OcrTextLine> lines,
        string note) =>
        new(documentType, source, null, ocrInput, Pages(lines), [], new ExtractionCoverage(0, 0, 0), note);

    private static IReadOnlyList<DiagnosedPage> Pages(IReadOnlyList<OcrTextLine> lines) =>
    [
        .. lines
            .GroupBy(line => line.PageNumber)
            .OrderBy(group => group.Key)
            .Select(group => new DiagnosedPage(
                group.Key,
                [.. group.Select(line => new DiagnosedBlock(line.Index, line.Text, line.Confidence, line.BoundingBox))]))
    ];

    private static DiagnosedField Field(
        string path,
        ExtractedFieldValue value,
        FieldScope? scope,
        IReadOnlyDictionary<string, string> recordedStatuses)
    {
        var (reason, explanation) = Explain(value, scope);

        return new DiagnosedField(
            path,
            value.ValidationStatus,
            reason,
            explanation,
            value.ValidationMessages ?? [],
            value.Raw,
            value.Normalized,
            value.Confidence,
            value.PageNumber,
            value.BoundingBox,
            recordedStatuses.TryGetValue(path, out var recorded) ? recorded : null,
            scope is null ? null : Rule(scope, value));
    }

    private static DiagnosedRule Rule(FieldScope scope, ExtractedFieldValue value) =>
        new(
            scope.Labels,
            scope.CandidateCount,
            [.. scope.Candidates.Select(candidate => new DiagnosedCandidate(
                candidate.LineIndex,
                candidate.Page,
                candidate.Text,
                candidate.Penalty,
                value.Raw is not null && value.Raw.Contains(candidate.Text.Trim(), StringComparison.Ordinal)))]);

    private static (string Reason, string Explanation) Explain(ExtractedFieldValue value, FieldScope? scope)
    {
        var messages = value.ValidationMessages ?? [];

        switch (value.ValidationStatus)
        {
            case "VALID":
                return messages.Contains(FieldFactory.NoLabelNearby)
                    ? (ExtractionReasons.FoundWithoutLabel, "The value was found, but no label was next to it, so the confidence is lower.")
                    : (ExtractionReasons.Found, "The value was read.");

            case "INVALID":
                return (
                    ExtractionReasons.ValidationFailed,
                    $"A value was read but failed validation: {Join(messages)}. The value as read is kept in raw.");

            case "UNCERTAIN":
                return (
                    ExtractionReasons.ValueUncertain,
                    $"A value was read, but the reading is an assumption: {Join(messages)}.");
        }

        if (scope is null)
        {
            return (
                ExtractionReasons.PatternNotFound,
                "This field is not read by label, and its pattern was not found in the text.");
        }

        var tried = string.Join(", ", scope.Labels.Select(label => label.Label));
        var present = scope.Labels.Where(label => label.LineIndexes.Count > 0).ToArray();

        if (present.Length == 0)
        {
            return (
                ExtractionReasons.LabelNotFound,
                $"None of the labels was found in the text: {tried}.");
        }

        var where = string.Join(", ", present.SelectMany(label => label.LineIndexes).Distinct().Order());

        return scope.CandidateCount == 0
            ? (
                ExtractionReasons.ValueEmpty,
                $"The label was found on line {where}, but no value was next to it, to its right or below it.")
            : (
                ExtractionReasons.ValueRejected,
                $"The label was found on line {where} and {scope.CandidateCount} candidate value(s) were examined, but none had the expected format.");
    }

    private static string Join(IReadOnlyList<string> messages) =>
        messages.Count == 0 ? "no detail" : string.Join(", ", messages);
}
