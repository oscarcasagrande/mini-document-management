using System.Text.RegularExpressions;
using DocReader.Application.Abstractions;
using DocReader.Domain.Validation;

namespace DocReader.Application.Extraction;

/// <summary>
/// Extrator do Certificado da Condição de Microempreendedor Individual (CCMEI), conforme
/// <c>schemas/documents/BR_CCMEI.v1.json</c>. O CNPJ e o CPF do empresário passam pelo dígito verificador.
///
/// O endereço comercial ocupa várias linhas: elas são juntas, e o CEP, a cidade e a UF saem do
/// texto juntado, porque o certificado não os separa em campos.
/// </summary>
public sealed class BrCcmeiExtractor(TimeProvider timeProvider) : IDocumentExtractor
{
    public const string TypeName = "BR_CCMEI";

    public const string ExtractorVersion = "br-ccmei-1.0.0";

    private const int AddressLines = 3;

    private static readonly string[] CnpjLabels = ["NUMERO DO CNPJ", "CNPJ"];
    private static readonly string[] LegalNameLabels = ["NOME EMPRESARIAL"];
    private static readonly string[] TradeNameLabels = ["NOME FANTASIA"];
    private static readonly string[] OpeningLabels = ["DATA DE ABERTURA", "DATA DE INICIO DE ATIVIDADE"];
    private static readonly string[] CapitalLabels = ["CAPITAL SOCIAL"];
    private static readonly string[] ActivityLabels = ["OCUPACAO PRINCIPAL", "ATIVIDADE ECONOMICA PRINCIPAL", "ATIVIDADE PRINCIPAL"];
    private static readonly string[] AddressLabels = ["ENDERECO COMERCIAL", "ENDERECO"];
    private static readonly string[] HolderNameLabels = ["NOME DO EMPRESARIO", "NOME DO TITULAR", "NOME"];
    private static readonly string[] HolderCpfLabels = ["CPF DO EMPRESARIO", "CPF"];
    private static readonly string[] HolderBirthLabels = ["DATA DE NASCIMENTO", "NASCIMENTO"];
    private static readonly string[] CertificateLabels = ["DATA DE EMISSAO", "EMITIDO EM"];

    private static readonly string[] KnownLabels =
    [
        .. CnpjLabels, .. LegalNameLabels, .. TradeNameLabels, .. OpeningLabels, .. CapitalLabels,
        .. ActivityLabels, .. AddressLabels, .. HolderNameLabels, .. HolderCpfLabels, .. HolderBirthLabels,
        .. CertificateLabels,
        "DADOS DO EMPRESARIO", "EMPRESARIO", "FORMA DE ATUACAO", "OCUPACOES SECUNDARIAS", "CCMEI", "PORTAL DO EMPREENDEDOR",
        "CERTIFICADO DA CONDICAO DE MICROEMPREENDEDOR INDIVIDUAL", "REPUBLICA FEDERATIVA DO BRASIL",
        "AMOSTRA SINTETICA - SEM VALOR LEGAL", "AMOSTRA SINTETICA SEM VALOR LEGAL"
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
        var (activityCode, activityDescription) = FieldReaders.Activity(search, ActivityLabels);
        var (address, postalCode, city, state) = ExtractAddress(search);

        var fields = new Dictionary<string, ExtractedFieldValue>(StringComparer.Ordinal)
        {
            ["cnpj"] = FieldReaders.Cnpj(search, CnpjLabels),
            ["legalName"] = FieldReaders.Text(search, LegalNameLabels, minimumLength: 3),
            ["tradeName"] = FieldReaders.Text(search, TradeNameLabels, minimumLength: 2),
            ["openingDate"] = FieldReaders.Date(search, OpeningLabels, DateKind.Issue, today),
            ["shareCapital"] = FieldReaders.Money(search, CapitalLabels),
            ["mainActivityCode"] = activityCode,
            ["mainActivityDescription"] = activityDescription,
            ["address"] = address,
            ["postalCode"] = postalCode,
            ["city"] = city,
            ["state"] = state,
            ["holderName"] = FieldReaders.Name(search, HolderNameLabels),
            ["holderCpf"] = FieldReaders.Cpf(search, HolderCpfLabels),
            ["holderBirthDate"] = FieldReaders.Date(search, HolderBirthLabels, DateKind.Birth, today),
            ["certificateDate"] = FieldReaders.Date(search, CertificateLabels, DateKind.Issue, today)
        };

        return Task.FromResult(new StructuredExtraction(
            TypeName, SchemaVersion, FieldFactory.OverallConfidence(fields.Values), fields));
    }

    private static (ExtractedFieldValue Address, ExtractedFieldValue PostalCode, ExtractedFieldValue City, ExtractedFieldValue State)
        ExtractAddress(LineSearch search)
    {
        foreach (var label in search.LabelLines(AddressLabels))
        {
            var parts = new List<(OcrTextLine Line, string Text)>();

            var colon = label.Text.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0 && label.Text[(colon + 1)..].Trim().Length > 0)
            {
                parts.Add((label, label.Text[(colon + 1)..].Trim()));
            }

            parts.AddRange(search.Following(label, AddressLines).Select(line => (line, line.Text.Trim())));

            if (parts.Count == 0)
            {
                continue;
            }

            var raw = string.Join(' ', parts.Select(part => part.Text));
            var normalized = TextNormalization.ForMatching(raw).Trim(' ', '-', ':', '.', ',');
            var evidence = parts[0].Line;

            var address = FieldFactory.Found(raw, normalized, evidence, 0m, FieldValidationStatus.Valid);
            var postalCode = PostalCodeFrom(raw, evidence);
            var (city, state) = CityAndStateFrom(parts);

            return (address, postalCode, city, state);
        }

        return (FieldFactory.NotFound(), FieldFactory.NotFound(), FieldFactory.NotFound(), FieldFactory.NotFound());
    }

    private static ExtractedFieldValue PostalCodeFrom(string text, OcrTextLine evidence)
    {
        var match = ExtractionPatterns.PostalCode().Match(text);

        return match.Success && PostalCode.TryNormalize(match.Value, out var normalized)
            ? FieldFactory.Found(match.Value, normalized, evidence, 0m, FieldValidationStatus.Valid, FieldReaders.PostalCodeValid)
            : FieldFactory.NotFound();
    }

    /// <summary>
    /// Cidade e UF, linha a linha: juntar as linhas colaria o bairro no nome da cidade quando o certificado
    /// as separa por quebra de linha em vez de vírgula.
    /// </summary>
    private static (ExtractedFieldValue City, ExtractedFieldValue State) CityAndStateFrom(
        IReadOnlyList<(OcrTextLine Line, string Text)> parts)
    {
        foreach (var (line, text) in parts)
        {
            var withoutPostalCode = ExtractionPatterns.PostalCode().Replace(TextNormalization.ForMatching(text), " ")
                .Replace("CEP", " ", StringComparison.Ordinal);

            foreach (Match match in ExtractionPatterns.CityAndState().Matches(withoutPostalCode))
            {
                var uf = match.Groups["uf"].Value;
                var cityName = match.Groups["city"].Value.Trim(' ', '-', '/', ',');

                if (BrazilianState.IsValid(uf) && cityName.Length >= 3)
                {
                    return (
                        FieldFactory.Found(cityName, cityName, line, 0.05m, FieldValidationStatus.Valid),
                        FieldFactory.Found(uf, uf, line, 0.05m, FieldValidationStatus.Valid, FieldReaders.StateValid));
                }
            }
        }

        return (FieldFactory.NotFound(), FieldFactory.NotFound());
    }
}
