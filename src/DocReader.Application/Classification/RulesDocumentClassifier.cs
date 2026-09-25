using System.Globalization;
using DocReader.Application.Abstractions;
using DocReader.Application.Extraction;
using DocReader.Application.Options;
using Microsoft.Extensions.Options;

namespace DocReader.Application.Classification;

/// <summary>
/// Classifier of RF-010 by weighted evidence, no model. Every profile scores the text: the evidence found
/// adds its weight, the counter-evidence found subtracts its penalty, and the type is accepted when the
/// score reaches its threshold. The best accepted type wins; without one the answer is UNKNOWN, never a
/// guess. Matching tolerates what real OCR does to a page (see <see cref="SearchableText"/>).
///
/// <see cref="Classify"/> and <see cref="Diagnose"/> share one code path, so the diagnostics can never
/// disagree with the decision that was recorded.
/// </summary>
public sealed class RulesDocumentClassifier : IDocumentClassifier
{
    public const string ClassifierVersion = "rules-2.0.0";

    private readonly IReadOnlyList<DocumentTypeProfile> _profiles;
    private readonly decimal _minimumScore;

    public RulesDocumentClassifier()
        : this(DocumentTypeProfile.All)
    {
    }

    public RulesDocumentClassifier(IOptions<ClassificationOptions> options)
        : this(DocumentTypeProfile.All, options.Value.MinimumScore)
    {
    }

    public RulesDocumentClassifier(IReadOnlyList<DocumentTypeProfile> profiles, decimal minimumScore = 0m)
    {
        _profiles = profiles;
        _minimumScore = minimumScore;
    }

    public string Version => ClassifierVersion;

    public ClassificationResult Classify(OcrResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var text = string.Join('\n', OcrTextLine.From(result).Select(line => line.Text));
        var diagnostics = Diagnose(text);

        if (diagnostics.Confidence is null)
        {
            return new ClassificationResult(ClassificationResult.UnknownType, null, [], ClassifierVersion);
        }

        var chosen = diagnostics.Candidates.First(candidate =>
            string.Equals(candidate.DocumentType, diagnostics.DocumentType, StringComparison.Ordinal));

        return new ClassificationResult(
            chosen.DocumentType,
            chosen.Score,
            [.. chosen.Evidence.Where(evidence => evidence.Matched).Select(evidence => evidence.Name)],
            ClassifierVersion);
    }

    public ClassificationDiagnostics Diagnose(string text)
    {
        var searchable = SearchableText.From(text);

        var candidates = _profiles
            .Select((profile, order) => (Candidate: Score(profile, searchable), Order: order))
            .OrderByDescending(entry => entry.Candidate.Score)
            .ThenBy(entry => entry.Order)
            .Select(entry => entry.Candidate)
            .ToList();

        var chosen = candidates.FirstOrDefault(candidate => candidate.Accepted);
        if (chosen is not null)
        {
            candidates = [.. candidates.Select(candidate => Explain(candidate, chosen))];
        }

        return new ClassificationDiagnostics(
            ClassifierVersion,
            chosen?.DocumentType ?? ClassificationResult.UnknownType,
            chosen?.Score,
            DecisionReason(chosen, candidates, searchable.Length),
            _minimumScore > 0 ? _minimumScore : null,
            searchable.Length,
            candidates);
    }

    private CandidateDiagnostics Score(DocumentTypeProfile profile, SearchableText text)
    {
        var evidence = Look(profile.Evidence, text);
        var counter = Look(profile.CounterEvidence, text);

        var gained = evidence.Where(item => item.Matched).Sum(item => item.Weight);
        var lost = counter.Where(item => item.Matched).Sum(item => item.Weight);
        var score = Math.Round(Math.Clamp(gained - lost, 0m, 1m), 4);

        var threshold = _minimumScore > 0 ? _minimumScore : profile.Threshold;
        var accepted = score >= threshold;

        return new CandidateDiagnostics(
            profile.DocumentType,
            score,
            threshold,
            accepted,
            CandidateReason(score, threshold, accepted, evidence, counter),
            evidence,
            counter);
    }

    private static List<EvidenceDiagnostics> Look(IReadOnlyList<ClassificationEvidence> evidence, SearchableText text) =>
        [.. evidence.Select(item =>
        {
            var match = text.FindAny(item.Patterns);

            return new EvidenceDiagnostics(
                item.Name,
                item.Weight,
                match is not null,
                match?.Pattern,
                match?.Kind,
                match?.Edits);
        })];

    private static string CandidateReason(
        decimal score,
        decimal threshold,
        bool accepted,
        IReadOnlyList<EvidenceDiagnostics> evidence,
        IReadOnlyList<EvidenceDiagnostics> counter)
    {
        var found = evidence.Where(item => item.Matched).Select(item => item.Name).ToArray();
        var missing = evidence.Where(item => !item.Matched).ToArray();
        var penalties = counter.Where(item => item.Matched).ToArray();

        var parts = new List<string>
        {
            accepted
                ? $"Score {Format(score)} reached the threshold {Format(threshold)}."
                : $"Score {Format(score)} is below the threshold {Format(threshold)} (short by {Format(threshold - score)})."
        };

        parts.Add(found.Length == 0 ? "No evidence found." : $"Found: {string.Join(", ", found)}.");

        if (penalties.Length > 0)
        {
            parts.Add(
                "Counter-evidence lowered the score: "
                + string.Join(", ", penalties.Select(item => $"{item.Name} (-{Format(item.Weight)})"))
                + ".");
        }

        if (!accepted && missing.Length > 0)
        {
            parts.Add(
                $"Not found (worth up to {Format(missing.Sum(item => item.Weight))}): "
                + string.Join(", ", missing.Select(item => item.Name))
                + ".");
        }

        return string.Join(' ', parts);
    }

    private static CandidateDiagnostics Explain(CandidateDiagnostics candidate, CandidateDiagnostics chosen) =>
        candidate.Accepted && !ReferenceEquals(candidate, chosen)
            ? candidate with
            {
                Reason = $"{candidate.Reason} Not chosen: {chosen.DocumentType} scored higher ({Format(chosen.Score)})."
            }
            : candidate;

    private static string DecisionReason(
        CandidateDiagnostics? chosen,
        IReadOnlyList<CandidateDiagnostics> candidates,
        int textLength)
    {
        if (chosen is not null)
        {
            return $"{chosen.DocumentType} chosen with score {Format(chosen.Score)} "
                + $"(threshold {Format(chosen.Threshold)}), best of {candidates.Count} types tried.";
        }

        if (textLength == 0)
        {
            return "The text is empty, so there was nothing to classify.";
        }

        var closest = candidates[0];

        return closest.Score <= 0
            ? "No evidence of any known type was found in the text."
            : $"No type reached its threshold. Closest: {closest.DocumentType} with score "
                + $"{Format(closest.Score)} of {Format(closest.Threshold)}.";
    }

    private static string Format(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
