using DocReader.Application.Abstractions;
using DocReader.Application.Extraction;

namespace DocReader.Application.Classification;

/// <summary>
/// First version of the classifier (RF-010): keywords, no model. A type needs all of its required
/// signals and no negative signal; the confidence starts at 0.6 with the required signals alone and
/// grows to 1.0 as the optional ones show up. Below the type's threshold the answer is UNKNOWN,
/// never a guess.
/// </summary>
public sealed class RulesDocumentClassifier : IDocumentClassifier
{
    public const string ClassifierVersion = "rules-1.0.0";

    private const decimal RequiredOnlyScore = 0.6m;

    private static readonly IReadOnlyList<DocumentTypeProfile> DefaultProfiles = [DocumentTypeProfile.BrCpfCard];

    private readonly IReadOnlyList<DocumentTypeProfile> _profiles;

    public RulesDocumentClassifier()
        : this(DefaultProfiles)
    {
    }

    public RulesDocumentClassifier(IReadOnlyList<DocumentTypeProfile> profiles)
    {
        _profiles = profiles;
    }

    public string Version => ClassifierVersion;

    public ClassificationResult Classify(OcrResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var text = TextNormalization.ForMatching(
            string.Join('\n', OcrTextLine.From(result).Select(line => line.Text)));

        ClassificationResult? best = null;

        foreach (var profile in _profiles)
        {
            var candidate = Score(profile, text);
            if (candidate is not null && (best is null || candidate.Confidence > best.Confidence))
            {
                best = candidate;
            }
        }

        return best ?? new ClassificationResult(ClassificationResult.UnknownType, null, [], ClassifierVersion);
    }

    private static ClassificationResult? Score(DocumentTypeProfile profile, string text)
    {
        if (profile.NegativeSignals.Any(signal => text.Contains(signal, StringComparison.Ordinal)))
        {
            return null;
        }

        if (!profile.RequiredSignals.All(signal => text.Contains(signal, StringComparison.Ordinal)))
        {
            return null;
        }

        var foundOptional = profile.OptionalSignals
            .Where(signal => text.Contains(signal, StringComparison.Ordinal))
            .ToArray();

        var optionalShare = profile.OptionalSignals.Count == 0
            ? 1m
            : (decimal)foundOptional.Length / profile.OptionalSignals.Count;

        var score = Math.Round(RequiredOnlyScore + ((1m - RequiredOnlyScore) * optionalShare), 4);
        if (score < profile.Threshold)
        {
            return null;
        }

        return new ClassificationResult(
            profile.DocumentType,
            score,
            [.. profile.RequiredSignals, .. foundOptional],
            ClassifierVersion);
    }
}
