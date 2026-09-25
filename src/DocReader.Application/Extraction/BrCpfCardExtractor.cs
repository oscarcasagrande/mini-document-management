using System.Text.RegularExpressions;
using DocReader.Application.Abstractions;
using DocReader.Domain;
using DocReader.Domain.Validation;

namespace DocReader.Application.Extraction;

/// <summary>
/// Extrator do cartão / comprovante de inscrição no CPF, conforme <c>schemas/documents/BR_CPF_CARD.v1.json</c>.
///
/// Trabalha por rótulo e vizinhança: procura o rótulo do campo e lê o valor na mesma linha ou na
/// linha seguinte. Quando o rótulo não aparece, cai para uma busca no documento inteiro e rebaixa a
/// confiança, porque o valor deixou de ter evidência de onde veio.
///
/// Campo não encontrado vira null com status NOT_FOUND. Nunca se inventa valor (RF-011).
/// </summary>
public sealed partial class BrCpfCardExtractor(TimeProvider timeProvider) : IDocumentExtractor
{
    public const string TypeName = "BR_CPF_CARD";

    public const string ExtractorVersion = "br-cpf-card-1.0.0";

    public const string CheckDigitValid = "CHECK_DIGIT_VALID";
    public const string CheckDigitInvalid = "CHECK_DIGIT_INVALID";
    public const string DateValid = "DATE_VALID";
    public const string NoLabelNearby = "NO_LABEL_NEARBY";

    /// <summary>Quanto a confiança cai quando o valor foi achado sem o rótulo ao lado.</summary>
    private const decimal FallbackConfidencePenalty = 0.30m;

    /// <summary>Linhas adiante do rótulo em que ainda se aceita encontrar o valor.</summary>
    private const int LabelLookahead = 2;

    private static readonly string[] CpfLabels = ["NUMERO DE INSCRICAO", "N DE INSCRICAO", "INSCRICAO", "CPF"];
    private static readonly string[] NameLabels = ["NOME"];
    private static readonly string[] BirthLabels = ["NASCIMENTO", "DATA DE NASCIMENTO"];

    /// <summary>
    /// Marcadores de linhas cuja data não é a de nascimento. No cartão, "INSCRICAO EM 02/09/2003"
    /// fica entre o rótulo NASCIMENTO e o valor, e o PP-OCRv5 pode devolvê-la antes do valor: sem
    /// este filtro a data de inscrição seria lida como nascimento.
    /// </summary>
    private static readonly string[] NonBirthDateMarkers = ["INSCRICAO EM", "DATA DE INSCRICAO", "EMITIDO EM", "EMISSAO"];

    /// <summary>
    /// Cabeçalhos do cartão que não são nome de pessoa. Sem esta lista, a busca por fallback pegaria
    /// "REPUBLICA FEDERATIVA DO BRASIL" como nome.
    /// </summary>
    private static readonly string[] HeaderLines =
    [
        "REPUBLICA FEDERATIVA DO BRASIL",
        "MINISTERIO DA FAZENDA",
        "SECRETARIA DA RECEITA FEDERAL",
        "CADASTRO DE PESSOAS FISICAS",
        "NUMERO DE INSCRICAO",
        "NASCIMENTO",
        "NOME",
        "AMOSTRA SINTETICA SEM VALOR LEGAL",
        "INSCRICAO EM"
    ];

    public string DocumentType => TypeName;

    public int SchemaVersion => 1;

    public string Version => ExtractorVersion;

    public Task<StructuredExtraction> ExtractAsync(OcrResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lines = OcrTextLine.From(result);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        var cpf = ExtractCpf(lines);
        var name = ExtractName(lines);
        var birthDate = ExtractBirthDate(lines, today);

        var fields = new Dictionary<string, ExtractedFieldValue>(StringComparer.Ordinal)
        {
            ["cpf"] = cpf,
            ["name"] = name,
            ["birthDate"] = birthDate
        };

        var found = fields.Values.Where(field => field.Confidence is not null).ToArray();
        decimal? overall = found.Length == 0
            ? null
            : Math.Round(found.Average(field => field.Confidence!.Value), 4);

        return Task.FromResult(new StructuredExtraction(TypeName, SchemaVersion, overall, fields));
    }

    private static ExtractedFieldValue ExtractCpf(IReadOnlyList<OcrTextLine> lines)
    {
        // Preferência 1: um CPF válido perto do rótulo.
        foreach (var (line, penalty) in CandidatesNear(lines, CpfLabels))
        {
            foreach (Match match in CpfPattern().Matches(line.Text))
            {
                if (Cpf.TryNormalize(match.Value, out var normalized) && Cpf.IsValid(normalized))
                {
                    return Found(match.Value, normalized, line, penalty, FieldValidationStatus.Valid, CheckDigitValid);
                }
            }
        }

        // Preferência 2: qualquer CPF válido no documento.
        foreach (var line in lines)
        {
            foreach (Match match in CpfPattern().Matches(line.Text))
            {
                if (Cpf.TryNormalize(match.Value, out var normalized) && Cpf.IsValid(normalized))
                {
                    return Found(match.Value, normalized, line, FallbackConfidencePenalty, FieldValidationStatus.Valid, CheckDigitValid);
                }
            }
        }

        // Preferência 3: algo com cara de CPF mas que não passa no dígito verificador. Reportar como
        // INVALID é mais útil do que reportar NOT_FOUND: diz que leu e reprovou, não que não achou.
        // A penalidade é a mesma nos dois casos, mas o aviso de rótulo ausente só vale quando o valor
        // realmente não estava perto de um rótulo.
        foreach (var (line, distancePenalty) in CandidatesNear(lines, CpfLabels))
        {
            var match = CpfPattern().Match(line.Text);
            if (match.Success && Cpf.TryNormalize(match.Value, out _))
            {
                return Build(match.Value, null, line, FallbackConfidencePenalty + distancePenalty, FieldValidationStatus.Invalid, [CheckDigitInvalid]);
            }
        }

        foreach (var line in lines)
        {
            var match = CpfPattern().Match(line.Text);
            if (match.Success && Cpf.TryNormalize(match.Value, out _))
            {
                return Found(match.Value, null, line, FallbackConfidencePenalty, FieldValidationStatus.Invalid, CheckDigitInvalid);
            }
        }

        return NotFound();
    }

    private static ExtractedFieldValue ExtractName(IReadOnlyList<OcrTextLine> lines)
    {
        foreach (var (line, penalty) in CandidatesNear(lines, NameLabels))
        {
            // O rótulo pode estar na mesma linha do valor: "NOME MARIA APARECIDA".
            var withoutLabel = StripLeadingLabel(line.Text, "NOME");
            var cleaned = TextNormalization.CleanPersonName(withoutLabel);

            if (cleaned is not null && !IsHeader(cleaned))
            {
                return Found(withoutLabel.Trim(), cleaned, line, penalty, FieldValidationStatus.Valid);
            }
        }

        // Fallback: a linha mais longa que parece nome e não é cabeçalho conhecido.
        var candidate = lines
            .Where(line => !IsHeader(line.Normalized))
            .Select(line => (Line: line, Name: TextNormalization.CleanPersonName(line.Text)))
            .Where(entry => entry.Name is not null && !IsHeader(entry.Name!))
            .OrderByDescending(entry => entry.Name!.Length)
            .FirstOrDefault();

        return candidate.Line is null
            ? NotFound()
            : Found(candidate.Line.Text, candidate.Name, candidate.Line, FallbackConfidencePenalty, FieldValidationStatus.Uncertain);
    }

    private static ExtractedFieldValue ExtractBirthDate(IReadOnlyList<OcrTextLine> lines, DateOnly today)
    {
        foreach (var (line, penalty) in CandidatesNear(lines, BirthLabels))
        {
            if (IsNonBirthDateLine(line))
            {
                continue;
            }

            foreach (Match match in DatePattern().Matches(line.Text))
            {
                if (BrazilianDate.TryParse(match.Value, out var parsed) &&
                    BrazilianDate.IsPlausibleBirthDate(parsed, today))
                {
                    return Found(match.Value, BrazilianDate.ToIso(parsed), line, penalty, FieldValidationStatus.Valid, DateValid);
                }
            }
        }

        // Fallback: a data plausível mais antiga do documento. Num cartão de CPF convivem a data de
        // nascimento e a data de inscrição, e a de nascimento é sempre a anterior.
        var candidates = lines
            .Where(line => !IsNonBirthDateLine(line))
            .SelectMany(line => DatePattern().Matches(line.Text).Select(match => (Line: line, Match: match)))
            .Select(entry => (entry.Line, entry.Match, Parsed: BrazilianDate.TryParse(entry.Match.Value, out var parsed) ? parsed : (DateOnly?)null))
            .Where(entry => entry.Parsed is not null && BrazilianDate.IsPlausibleBirthDate(entry.Parsed.Value, today))
            .OrderBy(entry => entry.Parsed!.Value)
            .ToArray();

        if (candidates.Length == 0)
        {
            return NotFound();
        }

        var chosen = candidates[0];

        return Found(
            chosen.Match.Value,
            BrazilianDate.ToIso(chosen.Parsed!.Value),
            chosen.Line,
            FallbackConfidencePenalty,
            FieldValidationStatus.Uncertain);
    }

    /// <summary>
    /// Devolve as linhas onde o valor pode estar, dado um conjunto de rótulos: a própria linha do
    /// rótulo e as <see cref="LabelLookahead"/> seguintes, com penalidade crescente de confiança.
    /// </summary>
    private static IEnumerable<(OcrTextLine Line, decimal Penalty)> CandidatesNear(
        IReadOnlyList<OcrTextLine> lines,
        string[] labels)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            if (!labels.Any(label => lines[index].Normalized.Contains(label, StringComparison.Ordinal)))
            {
                continue;
            }

            for (var offset = 0; offset <= LabelLookahead && index + offset < lines.Count; offset++)
            {
                var candidate = lines[index + offset];
                if (candidate.IsEmpty)
                {
                    continue;
                }

                yield return (candidate, offset * 0.05m);
            }
        }
    }

    private static string StripLeadingLabel(string text, string label)
    {
        var normalized = TextNormalization.ForMatching(text);
        if (!normalized.StartsWith(label, StringComparison.Ordinal))
        {
            return text;
        }

        var remainder = normalized[label.Length..].TrimStart(' ', ':', '-');

        return remainder.Length > 0 ? remainder : text;
    }

    private static bool IsNonBirthDateLine(OcrTextLine line) =>
        NonBirthDateMarkers.Any(marker => line.Normalized.Contains(marker, StringComparison.Ordinal));

    private static bool IsHeader(string normalized) =>
        HeaderLines.Any(header => normalized.Contains(header, StringComparison.Ordinal) || header.Contains(normalized, StringComparison.Ordinal));

    private static ExtractedFieldValue Found(
        string raw,
        string? normalized,
        OcrTextLine line,
        decimal penalty,
        FieldValidationStatus status,
        params string[] messages)
    {
        // Uma penalidade de fallback significa que o valor foi achado sem o rótulo ao lado.
        string[] reasons = penalty >= FallbackConfidencePenalty ? [.. messages, NoLabelNearby] : messages;

        return Build(raw, normalized, line, penalty, status, reasons);
    }

    private static ExtractedFieldValue Build(
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

    private static ExtractedFieldValue NotFound() =>
        new(null, null, null, EnumNaming.ToUpperSnakeCase(FieldValidationStatus.NotFound), null, []);

    /// <summary>CPF com ou sem máscara, exigindo fronteira para não casar pedaço de número maior.</summary>
    [GeneratedRegex(@"(?<!\d)\d{3}\.?\s?\d{3}\.?\s?\d{3}\s?-?\s?\d{2}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex CpfPattern();

    /// <summary>Data no formato dia/mês/ano com separador tolerante ao OCR.</summary>
    [GeneratedRegex(@"(?<!\d)\d{1,2}[/.\-]\d{1,2}[/.\-]\d{4}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();
}
