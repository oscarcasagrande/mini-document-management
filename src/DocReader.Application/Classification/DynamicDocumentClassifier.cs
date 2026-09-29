using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using Microsoft.Extensions.Options;

namespace DocReader.Application.Classification;

/// <summary>
/// The classifier the pipeline actually runs: loads the active <c>DocumentType</c> rows from the database
/// on every call and scores the text against them with <see cref="RulesDocumentClassifier"/>, so a rule
/// edited through <c>PUT /api/v1/document-types/{id}</c> applies to the very next document, without a
/// deploy or a restart. Rows whose <c>classificationRules</c> fails to parse are skipped rather than
/// failing the whole classification (they were already validated on write; this only guards against data
/// changed outside the API).
/// </summary>
public sealed class DynamicDocumentClassifier(
    IDocumentTypeRepository documentTypes,
    IOptions<ClassificationOptions> options) : IDocumentClassifier
{
    public string Version => RulesDocumentClassifier.ClassifierVersion;

    public async Task<ClassificationResult> ClassifyAsync(OcrResult result, CancellationToken ct)
    {
        var engine = await BuildEngineAsync(ct).ConfigureAwait(false);
        return engine.Classify(result);
    }

    public async Task<ClassificationDiagnostics> DiagnoseAsync(string text, CancellationToken ct)
    {
        var engine = await BuildEngineAsync(ct).ConfigureAwait(false);
        return engine.Diagnose(text);
    }

    private async Task<RulesDocumentClassifier> BuildEngineAsync(CancellationToken ct)
    {
        var active = await documentTypes.ListActiveAsync(ct).ConfigureAwait(false);

        var profiles = new List<DocumentTypeProfile>(active.Count);
        foreach (var type in active)
        {
            if (DocumentTypeProfile.TryParse(type.Code, type.ClassificationRulesJson, out var profile, out _))
            {
                profiles.Add(profile!);
            }
        }

        return new RulesDocumentClassifier(profiles, options.Value.MinimumScore);
    }
}
