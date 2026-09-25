using System.Text.RegularExpressions;
using DocReader.Application.Abstractions;
using DocReader.Domain.Validation;

namespace DocReader.Application.Extraction;

/// <summary>
/// Extrator do comprovante de residência (conta de energia, água, gás ou telecomunicações), conforme
/// <c>schemas/documents/BR_PROOF_OF_ADDRESS.v1.json</c>.
///
/// Cada concessionária desenha a fatura a seu modo, então o endereço é lido por rótulo e, sem rótulo,
/// pela forma da linha (logradouro no início, cidade e UF, CEP), com a confiança rebaixada. O tipo de
/// serviço vem de palavras-chave e só é afirmado quando uma família de palavras aparece sozinha.
/// </summary>
public sealed class BrProofOfAddressExtractor(TimeProvider timeProvider) : IDocumentExtractor
{
    public const string TypeName = "BR_PROOF_OF_ADDRESS";

    public const string ExtractorVersion = "br-proof-of-address-1.0.0";

    public const string ServiceTypeKeyword = "SERVICE_TYPE_KEYWORD";
    public const string ServiceTypeAmbiguous = "SERVICE_TYPE_AMBIGUOUS";
    public const string AddressByShape = "ADDRESS_BY_SHAPE";

    private static readonly string[] HolderLabels =
        ["NOME DO CLIENTE", "CLIENTE", "TITULAR", "TITULAR DA CONTA", "DESTINATARIO", "NOME"];
    private static readonly string[] DocumentLabels = ["CPF/CNPJ", "CPF / CNPJ", "CPF/CNPJ/RNE", "CNPJ", "CPF"];
    private static readonly string[] AddressLabels =
        ["ENDERECO", "ENDERECO DE ENTREGA", "ENDERECO DO CLIENTE", "ENDERECO DA UNIDADE CONSUMIDORA", "LOGRADOURO"];
    private static readonly string[] NeighborhoodLabels = ["BAIRRO", "BAIRRO/DISTRITO"];
    private static readonly string[] CityLabels = ["MUNICIPIO", "CIDADE"];
    private static readonly string[] StateLabels = ["UF", "ESTADO"];
    private static readonly string[] PostalCodeLabels = ["CEP"];
    private static readonly string[] ReferenceLabels = ["MES DE REFERENCIA", "MES/ANO", "REFERENCIA", "COMPETENCIA", "REF"];
    private static readonly string[] DueLabels = ["DATA DE VENCIMENTO", "VENCIMENTO"];

    private static readonly string[] KnownLabels =
    [
        .. HolderLabels, .. DocumentLabels, .. AddressLabels, .. NeighborhoodLabels, .. CityLabels,
        .. StateLabels, .. PostalCodeLabels, .. ReferenceLabels, .. DueLabels,
        "UNIDADE CONSUMIDORA", "TOTAL A PAGAR", "VALOR", "CONSUMO", "LEITURA ANTERIOR", "LEITURA ATUAL",
        "AMOSTRA SINTETICA - SEM VALOR LEGAL", "AMOSTRA SINTETICA SEM VALOR LEGAL"
    ];

    /// <summary>Famílias de serviço e as palavras que as identificam.</summary>
    private static readonly (string Service, string[] Keywords)[] ServiceKeywords =
    [
        ("ELECTRICITY", ["ENERGIA ELETRICA", "KWH", "DISTRIBUIDORA DE ENERGIA"]),
        ("WATER", ["AGUA E ESGOTO", "ABASTECIMENTO DE AGUA", "SANEAMENTO", "CONSUMO DE AGUA"]),
        ("GAS", ["GAS NATURAL", "GAS ENCANADO", "DISTRIBUIDORA DE GAS"]),
        ("TELECOM", ["INTERNET", "BANDA LARGA", "TELEFONIA", "FATURA DE TELEFONE", "TV POR ASSINATURA"])
    ];

    public string DocumentType => TypeName;

    public int SchemaVersion => 1;

    public string Version => ExtractorVersion;

    public Task<StructuredExtraction> ExtractAsync(OcrResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lines = OcrTextLine.From(result);
        var search = new LineSearch(lines, KnownLabels);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        var postalCode = ExtractPostalCode(search);
        var address = ExtractAddress(search, lines);
        var (city, state) = ExtractCityAndState(search, lines);

        var fields = new Dictionary<string, ExtractedFieldValue>(StringComparer.Ordinal)
        {
            ["holderName"] = FieldReaders.Name(search, HolderLabels),
            ["holderDocument"] = FieldReaders.CpfOrCnpj(search, DocumentLabels),
            ["addressLine"] = address,
            ["neighborhood"] = FieldReaders.Text(search, NeighborhoodLabels),
            ["city"] = city,
            ["state"] = state,
            ["postalCode"] = postalCode,
            ["referenceMonth"] = ExtractReferenceMonth(search),
            ["dueDate"] = FieldReaders.Date(search, DueLabels, DateKind.Any, today),
            ["serviceType"] = ExtractServiceType(lines)
        };

        return Task.FromResult(new StructuredExtraction(
            TypeName, SchemaVersion, FieldFactory.OverallConfidence(fields.Values), fields));
    }

    private static ExtractedFieldValue ExtractPostalCode(LineSearch search)
    {
        var byLabel = FieldReaders.PostalCode(search, PostalCodeLabels);
        if (byLabel.ValidationStatus == "VALID")
        {
            return byLabel;
        }

        foreach (var line in search.Lines)
        {
            var match = ExtractionPatterns.PostalCode().Match(line.Text);
            if (match.Success && PostalCode.TryNormalize(match.Value, out var normalized) && !ExtractionPatterns.Cpf().IsMatch(line.Text))
            {
                // "CEP 04000-000 SAO PAULO - SP": a palavra na própria linha é evidência de rótulo, só sem
                // o campo separado, então a confiança cai pouco e o aviso de rótulo ausente não se aplica.
                var labelled = line.Normalized.Contains("CEP", StringComparison.Ordinal);

                return labelled
                    ? FieldFactory.Build(match.Value, normalized, line, 0.05m, FieldValidationStatus.Valid, [FieldReaders.PostalCodeValid])
                    : FieldFactory.Found(match.Value, normalized, line, FieldFactory.FallbackConfidencePenalty, FieldValidationStatus.Valid, FieldReaders.PostalCodeValid);
            }
        }

        return FieldFactory.NotFound();
    }

    /// <summary>Por rótulo; sem rótulo, a primeira linha que começa como logradouro.</summary>
    private static ExtractedFieldValue ExtractAddress(LineSearch search, IReadOnlyList<OcrTextLine> lines)
    {
        foreach (var candidate in search.After(AddressLabels))
        {
            var cleaned = TextNormalization.ForMatching(candidate.Text).Trim(' ', '-', ':', '.', ',');
            if (cleaned.Length >= 5 && cleaned.Any(char.IsAsciiDigit) && cleaned.Any(char.IsAsciiLetter))
            {
                return FieldFactory.Found(candidate.Text.Trim(), cleaned, candidate.Line, candidate.Penalty, FieldValidationStatus.Valid);
            }
        }

        foreach (var line in lines)
        {
            if (ExtractionPatterns.StreetStart().IsMatch(line.Normalized) && line.Normalized.Any(char.IsAsciiDigit))
            {
                return FieldFactory.Found(
                    line.Text.Trim(), line.Normalized, line, FieldFactory.FallbackConfidencePenalty,
                    FieldValidationStatus.Valid, AddressByShape);
            }
        }

        return FieldFactory.NotFound();
    }

    /// <summary>
    /// Cidade e UF pelos rótulos e, sem eles, pela linha do CEP ou pela linha logo abaixo do
    /// endereço, no formato "SAO PAULO - SP". A UF só vale se for uma das 27.
    /// </summary>
    private static (ExtractedFieldValue City, ExtractedFieldValue State) ExtractCityAndState(
        LineSearch search,
        IReadOnlyList<OcrTextLine> lines)
    {
        var city = FieldReaders.Text(search, CityLabels, minimumLength: 3);
        var state = FieldReaders.State(search, StateLabels);

        if (city.ValidationStatus == "VALID" && state.ValidationStatus == "VALID")
        {
            return (city, state);
        }

        foreach (var line in lines)
        {
            var text = ExtractionPatterns.PostalCode().Replace(line.Normalized, " ").Replace("CEP", " ", StringComparison.Ordinal);

            foreach (Match match in ExtractionPatterns.CityAndState().Matches(text))
            {
                var uf = match.Groups["uf"].Value;
                if (!BrazilianState.IsValid(uf))
                {
                    continue;
                }

                var cityName = match.Groups["city"].Value.Trim(' ', '-', '/', ',');
                if (cityName.Length < 3)
                {
                    continue;
                }

                var penalty = FieldFactory.FallbackConfidencePenalty;
                if (city.ValidationStatus != "VALID")
                {
                    city = FieldFactory.Found(cityName, cityName, line, penalty, FieldValidationStatus.Valid);
                }

                if (state.ValidationStatus != "VALID")
                {
                    state = FieldFactory.Found(uf, uf, line, penalty, FieldValidationStatus.Valid, FieldReaders.StateValid);
                }

                return (city, state);
            }
        }

        return (city, state);
    }

    private static ExtractedFieldValue ExtractReferenceMonth(LineSearch search)
    {
        foreach (var candidate in search.After(ReferenceLabels))
        {
            foreach (Match match in ExtractionPatterns.MonthYear().Matches(candidate.Text))
            {
                if (BrazilianDate.TryParseMonthYear(match.Value, out var isoMonth))
                {
                    return FieldFactory.Found(match.Value, isoMonth, candidate.Line, candidate.Penalty, FieldValidationStatus.Valid, FieldReaders.DateValid);
                }
            }
        }

        return FieldFactory.NotFound();
    }

    private static ExtractedFieldValue ExtractServiceType(IReadOnlyList<OcrTextLine> lines)
    {
        var hits = new List<(string Service, OcrTextLine Line)>();

        foreach (var (service, keywords) in ServiceKeywords)
        {
            var line = lines.FirstOrDefault(candidate => keywords.Any(keyword => candidate.Normalized.Contains(keyword, StringComparison.Ordinal)));
            if (line is not null)
            {
                hits.Add((service, line));
            }
        }

        return hits.Count switch
        {
            0 => FieldFactory.NotFound(),
            1 => FieldFactory.Found(hits[0].Line.Text.Trim(), hits[0].Service, hits[0].Line, 0.10m, FieldValidationStatus.Valid, ServiceTypeKeyword),
            _ => FieldFactory.Build(hits[0].Line.Text.Trim(), null, hits[0].Line, 0.10m, FieldValidationStatus.Uncertain, [ServiceTypeAmbiguous])
        };
    }
}
