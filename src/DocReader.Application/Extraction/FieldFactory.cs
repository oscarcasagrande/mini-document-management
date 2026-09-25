using DocReader.Application.Abstractions;
using DocReader.Domain;
using DocReader.Domain.Validation;

namespace DocReader.Application.Extraction;

/// <summary>
/// Monta o <see cref="ExtractedFieldValue"/> de todos os extratores, para que confiança, evidência e
/// avisos sigam a mesma regra em todos os tipos documentais.
/// </summary>
internal static class FieldFactory
{
    public const string NoLabelNearby = "NO_LABEL_NEARBY";

    /// <summary>Quanto a confiança cai quando o valor foi achado sem o rótulo ao lado.</summary>
    public const decimal FallbackConfidencePenalty = 0.30m;

    /// <summary>
    /// Campo encontrado. Uma penalidade de fallback significa que o valor foi achado sem rótulo, e o
    /// aviso <see cref="NoLabelNearby"/> acompanha a confiança menor.
    /// </summary>
    public static ExtractedFieldValue Found(
        string raw,
        string? normalized,
        OcrTextLine line,
        decimal penalty,
        FieldValidationStatus status,
        params string[] messages)
    {
        string[] reasons = penalty >= FallbackConfidencePenalty ? [.. messages, NoLabelNearby] : messages;

        return Build(raw, normalized, line, penalty, status, reasons);
    }

    /// <summary>Como <see cref="Found"/>, com os motivos exatos, sem deduzir aviso pela penalidade.</summary>
    public static ExtractedFieldValue Build(
        string raw,
        string? normalized,
        OcrTextLine line,
        decimal penalty,
        FieldValidationStatus status,
        string[] reasons)
    {
        var baseConfidence = line.Confidence ?? 0.80m;
        var confidence = Math.Clamp(baseConfidence - penalty, 0.01m, 1.00m);

        return new ExtractedFieldValue(
            raw,
            normalized,
            Math.Round(confidence, 4),
            EnumNaming.ToUpperSnakeCase(status),
            line.PageNumber,
            line.BoundingBox,
            reasons);
    }

    public static ExtractedFieldValue NotFound() =>
        new(null, null, null, EnumNaming.ToUpperSnakeCase(FieldValidationStatus.NotFound), null, []);

    /// <summary>Média da confiança dos campos encontrados, ou null quando nenhum foi.</summary>
    public static decimal? OverallConfidence(IEnumerable<ExtractedFieldValue> fields)
    {
        var found = fields.Where(field => field.Confidence is not null).ToArray();

        return found.Length == 0
            ? null
            : Math.Round(found.Average(field => field.Confidence!.Value), 4);
    }
}
