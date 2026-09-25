using System.Globalization;
using System.Text.RegularExpressions;
using DocReader.Application.Abstractions;
using DocReader.Domain.Validation;

namespace DocReader.Application.Extraction;

/// <summary>
/// Extrator do contrato social, conforme <c>schemas/documents/BR_SOCIAL_CONTRACT.v1.json</c>.
///
/// Contrato é texto corrido: a frase quebra em linhas de OCR e o valor vem depois de uma expressão
/// ("com sede na", "capital social de"), não de um rótulo. As linhas são juntas e as expressões são
/// procuradas no texto todo; a evidência de cada campo é a linha onde o trecho começa.
///
/// Sócios saem como campos indexados, <c>partners[0].name</c> e <c>partners[0].cpf</c>. O CNPJ pode faltar:
/// o contrato de constituição é anterior à inscrição, e nesse caso o campo é NOT_FOUND, não um chute.
/// </summary>
public sealed partial class BrSocialContractExtractor(TimeProvider timeProvider) : IDocumentExtractor
{
    public const string TypeName = "BR_SOCIAL_CONTRACT";

    public const string ExtractorVersion = "br-social-contract-1.0.0";

    public const int MaxPartners = 6;

    private const int CpfWindow = 400;

    public string DocumentType => TypeName;

    public int SchemaVersion => 1;

    public string Version => ExtractorVersion;

    public Task<StructuredExtraction> ExtractAsync(OcrResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lines = OcrTextLine.From(result);
        var prose = new ProseIndex(lines);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        var fields = new Dictionary<string, ExtractedFieldValue>(StringComparer.Ordinal);

        if (lines.Count > 0)
        {
            fields["companyName"] = ExtractCompanyName(prose);
            fields["cnpj"] = ExtractCnpj(prose);
            fields["nire"] = ExtractNire(prose);
            fields["shareCapital"] = ExtractShareCapital(prose);

            var (headquarters, postalCode) = ExtractHeadquarters(prose);
            fields["headquarters"] = headquarters;
            fields["headquartersPostalCode"] = postalCode;
            fields["corporatePurpose"] = ExtractPurpose(prose);
            fields["contractDate"] = ExtractContractDate(prose, today);

            foreach (var (path, value) in ExtractPartners(prose))
            {
                fields[path] = value;
            }
        }

        return Task.FromResult(new StructuredExtraction(
            TypeName, SchemaVersion, FieldFactory.OverallConfidence(fields.Values), fields));
    }

    private static ExtractedFieldValue ExtractCompanyName(ProseIndex prose)
    {
        foreach (var match in prose.Matches(CompanyNamePattern()))
        {
            var group = match.Match.Groups["n"];
            var folded = prose.Folded.Substring(group.Index, group.Length);
            var line = prose.LineAt(group.Index);

            return FieldFactory.Found(
                Collapse(prose.Original.Substring(group.Index, group.Length)),
                Collapse(folded),
                line,
                0m,
                FieldValidationStatus.Valid);
        }

        return FieldFactory.NotFound();
    }

    /// <summary>
    /// CNPJ válido perto da palavra "CNPJ" tem confiança cheia. Sem essa âncora, ou com dígito reprovado,
    /// o resultado cai para o que o texto tiver, com a confiança rebaixada e o aviso de falta de âncora.
    /// </summary>
    private static ExtractedFieldValue ExtractCnpj(ProseIndex prose)
    {
        ProseMatch? anchoredInvalid = null;
        ProseMatch? unanchoredValid = null;

        foreach (var match in prose.Matches(ExtractionPatterns.Cnpj(), onOriginal: true))
        {
            if (!Cnpj.TryNormalize(match.Match.Value, out var normalized))
            {
                continue;
            }

            var anchored = HasAnchor(prose.Folded, match.Match.Index, "CNPJ");

            if (Cnpj.IsValid(normalized))
            {
                if (anchored)
                {
                    return FieldFactory.Found(match.Match.Value, normalized, match.Line, 0m, FieldValidationStatus.Valid, FieldReaders.CheckDigitValid);
                }

                unanchoredValid ??= match;
            }
            else if (anchored)
            {
                anchoredInvalid ??= match;
            }
        }

        if (unanchoredValid is not null)
        {
            var normalized = Cnpj.TryNormalize(unanchoredValid.Match.Value, out var value) ? value : null;
            return FieldFactory.Found(unanchoredValid.Match.Value, normalized, unanchoredValid.Line, FieldFactory.FallbackConfidencePenalty, FieldValidationStatus.Valid, FieldReaders.CheckDigitValid);
        }

        return anchoredInvalid is null
            ? FieldFactory.NotFound()
            : FieldFactory.Build(anchoredInvalid.Match.Value, null, anchoredInvalid.Line, FieldFactory.FallbackConfidencePenalty, FieldValidationStatus.Invalid, [FieldReaders.CheckDigitInvalid]);
    }

    private static ExtractedFieldValue ExtractNire(ProseIndex prose)
    {
        foreach (var match in prose.Matches(NirePattern()))
        {
            var group = match.Match.Groups["v"];

            return FieldFactory.Found(
                prose.Original.Substring(group.Index, group.Length),
                group.Value,
                prose.LineAt(group.Index),
                0m,
                FieldValidationStatus.Valid,
                FieldReaders.FormatValid);
        }

        return FieldFactory.NotFound();
    }

    private static ExtractedFieldValue ExtractShareCapital(ProseIndex prose)
    {
        foreach (var match in prose.Matches(ShareCapitalPattern()))
        {
            var group = match.Match.Groups["v"];
            if (BrazilianMoney.TryParse(group.Value, out var amount))
            {
                return FieldFactory.Found(
                    prose.Original.Substring(group.Index, group.Length),
                    BrazilianMoney.ToInvariant(amount),
                    prose.LineAt(group.Index),
                    0m,
                    FieldValidationStatus.Valid,
                    FieldReaders.FormatValid);
            }
        }

        return FieldFactory.NotFound();
    }

    private static (ExtractedFieldValue Headquarters, ExtractedFieldValue PostalCode) ExtractHeadquarters(ProseIndex prose)
    {
        foreach (var match in prose.Matches(HeadquartersPattern()))
        {
            var group = match.Match.Groups["a"];
            var line = prose.LineAt(group.Index);
            var raw = Collapse(prose.Original.Substring(group.Index, group.Length));
            var normalized = Collapse(prose.Folded.Substring(group.Index, group.Length));

            var headquarters = FieldFactory.Found(raw, normalized, line, 0m, FieldValidationStatus.Valid);

            var cep = ExtractionPatterns.PostalCode().Matches(normalized).LastOrDefault();
            var postalCode = cep is not null && PostalCode.TryNormalize(cep.Value, out var digits)
                ? FieldFactory.Found(cep.Value, digits, line, 0m, FieldValidationStatus.Valid, FieldReaders.PostalCodeValid)
                : FieldFactory.NotFound();

            return (headquarters, postalCode);
        }

        return (FieldFactory.NotFound(), FieldFactory.NotFound());
    }

    private static ExtractedFieldValue ExtractPurpose(ProseIndex prose)
    {
        foreach (var match in prose.Matches(PurposePattern()))
        {
            var group = match.Match.Groups["v"];
            var raw = Collapse(prose.Original.Substring(group.Index, group.Length));

            return FieldFactory.Found(
                raw,
                Collapse(prose.Folded.Substring(group.Index, group.Length)),
                prose.LineAt(group.Index),
                0m,
                FieldValidationStatus.Valid);
        }

        return FieldFactory.NotFound();
    }

    /// <summary>A data por extenso que fecha o instrumento é a última do texto.</summary>
    private static ExtractedFieldValue ExtractContractDate(ProseIndex prose, DateOnly today)
    {
        var match = prose.Matches(ExtractionPatterns.LongDate()).LastOrDefault();
        if (match is null)
        {
            return FieldFactory.NotFound();
        }

        if (BrazilianDate.TryParseLongForm(match.Match.Value, out var parsed) && BrazilianDate.IsPlausibleBirthDate(parsed, today))
        {
            return FieldFactory.Found(match.Original, BrazilianDate.ToIso(parsed), match.Line, 0m, FieldValidationStatus.Valid, FieldReaders.DateValid);
        }

        return FieldFactory.Build(match.Original, null, match.Line, FieldFactory.FallbackConfidencePenalty, FieldValidationStatus.Invalid, [FieldReaders.DateInvalid]);
    }

    /// <summary>
    /// Sócios: nome próprio seguido da qualificação ("MARIA, brasileira, casada") e o primeiro CPF depois
    /// dele. O nome é procurado no texto original porque só a caixa distingue nome de frase.
    /// </summary>
    private static IEnumerable<(string Path, ExtractedFieldValue Value)> ExtractPartners(ProseIndex prose)
    {
        var matches = prose.Matches(PartnerPattern(), onOriginal: true).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        for (var position = 0; position < matches.Length && index < MaxPartners; position++)
        {
            var match = matches[position];
            var group = match.Match.Groups["n"];
            var cleaned = TextNormalization.CleanPersonName(group.Value);

            if (cleaned is null || !seen.Add(cleaned))
            {
                continue;
            }

            var line = prose.LineAt(group.Index);
            yield return (
                string.Create(CultureInfo.InvariantCulture, $"partners[{index}].name"),
                FieldFactory.Found(Collapse(group.Value), cleaned, line, 0m, FieldValidationStatus.Valid));

            var windowStart = match.Match.Index + match.Match.Length;
            var windowEnd = position + 1 < matches.Length
                ? matches[position + 1].Match.Index
                : Math.Min(prose.Original.Length, windowStart + CpfWindow);
            windowEnd = Math.Min(windowEnd, windowStart + CpfWindow);

            var window = prose.Original[windowStart..Math.Max(windowStart, windowEnd)];
            var cpfMatch = ExtractionPatterns.Cpf().Match(window);
            var cpfLine = prose.LineAt(windowStart + (cpfMatch.Success ? cpfMatch.Index : 0));

            var cpf = cpfMatch.Success && Cpf.TryNormalize(cpfMatch.Value, out var digits)
                ? (Cpf.IsValid(digits)
                    ? FieldFactory.Found(cpfMatch.Value, digits, cpfLine, 0m, FieldValidationStatus.Valid, FieldReaders.CheckDigitValid)
                    : FieldFactory.Build(cpfMatch.Value, null, cpfLine, FieldFactory.FallbackConfidencePenalty, FieldValidationStatus.Invalid, [FieldReaders.CheckDigitInvalid]))
                : FieldFactory.NotFound();

            yield return (string.Create(CultureInfo.InvariantCulture, $"partners[{index}].cpf"), cpf);
            index++;
        }
    }

    /// <summary>Há a palavra âncora nos 40 caracteres que antecedem o valor.</summary>
    private static bool HasAnchor(string folded, int valueIndex, string anchor)
    {
        var start = Math.Max(0, valueIndex - 40);

        return folded.AsSpan(start, valueIndex - start).Contains(anchor.AsSpan(), StringComparison.Ordinal);
    }

    private static string Collapse(string value) =>
        WhitespaceRun().Replace(value.Trim(), " ").Trim(' ', ',', ';', '.');

    /// <summary>
    /// "denominação social de X LTDA" ou "sob a firma X LTDA": o nome termina no sufixo societário, que é o
    /// que o separa do resto da frase. O título "contrato social de constituição de sociedade limitada"
    /// fica de fora de propósito: a expressão é comprida e apanharia palavras que não são o nome.
    /// </summary>
    [GeneratedRegex(
        @"(?:DENOMINACAO(?: SOCIAL)?|NOME EMPRESARIAL|RAZAO SOCIAL|FIRMA)\s+(?:DE\s+|DA\s+|DO\s+)?(?<n>(?:[A-Z0-9&'\-]+\s+){1,8}?(?:LTDA\.?|EIRELI|SLU|EPP|S/A|S\.A\.?))(?![A-Z])",
        RegexOptions.CultureInvariant)]
    private static partial Regex CompanyNamePattern();

    [GeneratedRegex(@"NIRE\s*(?:N[O°º.]*\s*)?:?\s*(?<v>\d{11})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex NirePattern();

    [GeneratedRegex(@"CAPITAL SOCIAL[^R0-9]{0,120}?(?<v>R\$\s?\d{1,3}(?:\.\d{3})*,\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex ShareCapitalPattern();

    [GeneratedRegex(
        @"SEDE\s+(?:SOCIAL\s+)?(?:NA|NO|EM|A)\s+(?<a>(?:RUA|AVENIDA|AV\.?|ALAMEDA|TRAVESSA|PRACA|RODOVIA|ESTRADA)\b.{5,220}?CEP\s*[N°º.]*\s*\d{2}\.?\d{3}-?\d{3})",
        RegexOptions.CultureInvariant)]
    private static partial Regex HeadquartersPattern();

    /// <summary>"objeto social será X." até o ponto final da frase, sem parar no ponto de um código CNAE.</summary>
    [GeneratedRegex(
        @"OBJETO(?: SOCIAL)?(?: DA SOCIEDADE)?\s+(?:SERA|SERAO|E)\s*:?\s*(?<v>.{10,300}?)(?<!\d)\.\s",
        RegexOptions.CultureInvariant)]
    private static partial Regex PurposePattern();

    [GeneratedRegex(
        @"(?<=(?:^|[,;:.]\s+|\d\)\s+|\b(?:e|E|entre|por)\s+))(?<n>\p{Lu}[\p{L}'\-]+(?:\s+(?:d[aeo]s?|e|\p{Lu}[\p{L}'\-]+)){1,7}),\s+(?=brasileir|portugu|estrangeir|natural|casad|solteir|divorciad|vi[uú]v|empres[aá]ri|maior|nascid)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PartnerPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();
}
