using DocReader.Application.Abstractions;

namespace DocReader.Application.Extraction;

/// <summary>
/// Extrator do comprovante de inscrição e de situação cadastral do CNPJ, conforme
/// <c>schemas/documents/BR_CNPJ_CARD.v1.json</c>.
///
/// O cartão é uma grade de caixas com o rótulo em letra pequena em cima e o valor logo abaixo, então a
/// leitura é sobretudo espacial. O CNPJ passa pelo dígito verificador do RF-012, inclusive o alfanumérico.
/// </summary>
public sealed class BrCnpjCardExtractor(TimeProvider timeProvider) : IDocumentExtractor
{
    public const string TypeName = "BR_CNPJ_CARD";

    public const string ExtractorVersion = "br-cnpj-card-1.1.0";

    private static readonly string[] CnpjLabels = ["NUMERO DE INSCRICAO", "CNPJ"];
    private static readonly string[] OpeningLabels = ["DATA DE ABERTURA"];
    private static readonly string[] LegalNameLabels = ["NOME EMPRESARIAL"];
    private static readonly string[] TradeNameLabels =
        ["TITULO DO ESTABELECIMENTO (NOME DE FANTASIA)", "TITULO DO ESTABELECIMENTO", "NOME DE FANTASIA", "NOME FANTASIA"];
    private static readonly string[] ActivityLabels =
        ["CODIGO E DESCRICAO DA ATIVIDADE ECONOMICA PRINCIPAL", "ATIVIDADE ECONOMICA PRINCIPAL"];
    private static readonly string[] LegalNatureLabels = ["CODIGO E DESCRICAO DA NATUREZA JURIDICA", "NATUREZA JURIDICA"];
    private static readonly string[] StreetLabels = ["LOGRADOURO"];
    private static readonly string[] NumberLabels = ["NUMERO"];
    private static readonly string[] ComplementLabels = ["COMPLEMENTO"];
    private static readonly string[] PostalCodeLabels = ["CEP"];
    private static readonly string[] NeighborhoodLabels = ["BAIRRO/DISTRITO", "BAIRRO"];
    private static readonly string[] CityLabels = ["MUNICIPIO"];
    private static readonly string[] StateLabels = ["UF"];
    private static readonly string[] StatusLabels = ["SITUACAO CADASTRAL"];
    private static readonly string[] StatusDateLabels = ["DATA DA SITUACAO CADASTRAL"];

    private static readonly string[] KnownLabels =
    [
        .. CnpjLabels, .. OpeningLabels, .. LegalNameLabels, .. TradeNameLabels, .. ActivityLabels,
        .. LegalNatureLabels, .. StreetLabels, .. NumberLabels, .. ComplementLabels, .. PostalCodeLabels,
        .. NeighborhoodLabels, .. CityLabels, .. StateLabels, .. StatusLabels, .. StatusDateLabels,
        "REPUBLICA FEDERATIVA DO BRASIL", "CADASTRO NACIONAL DA PESSOA JURIDICA", "COMPROVANTE DE INSCRICAO E DE SITUACAO CADASTRAL",
        "PORTE", "ENDERECO ELETRONICO", "TELEFONE", "ENTE FEDERATIVO RESPONSAVEL (EFR)", "CODIGO E DESCRICAO DAS ATIVIDADES ECONOMICAS SECUNDARIAS",
        "MOTIVO DE SITUACAO CADASTRAL", "SITUACAO ESPECIAL", "DATA DA SITUACAO ESPECIAL", "MATRIZ", "FILIAL",
        "AMOSTRA SINTETICA - SEM VALOR LEGAL", "AMOSTRA SINTETICA SEM VALOR LEGAL"
    ];

    public string DocumentType => TypeName;

    public int SchemaVersion => 1;

    public string Version => ExtractorVersion;

    public Task<StructuredExtraction> ExtractAsync(OcrResult result, CancellationToken ct) => ExtractAsync(result, null, ct);

    public Task<StructuredExtraction> ExtractAsync(OcrResult result, ExtractionTrace? trace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        var search = new LineSearch(OcrTextLine.From(result), KnownLabels, trace);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var (activityCode, activityDescription) = search.Fields(
            ["mainActivityCode", "mainActivityDescription"], () => FieldReaders.Activity(search, ActivityLabels));

        var fields = new Dictionary<string, ExtractedFieldValue>(StringComparer.Ordinal)
        {
            ["cnpj"] = search.Field("cnpj", () => FieldReaders.Cnpj(search, CnpjLabels)),
            ["openingDate"] = search.Field("openingDate", () => FieldReaders.Date(search, OpeningLabels, DateKind.Issue, today)),
            ["legalName"] = search.Field("legalName", () => FieldReaders.Text(search, LegalNameLabels, minimumLength: 3)),
            ["tradeName"] = search.Field("tradeName", () => FieldReaders.Text(search, TradeNameLabels, minimumLength: 2)),
            ["mainActivityCode"] = activityCode,
            ["mainActivityDescription"] = activityDescription,
            ["legalNature"] = search.Field("legalNature", () => FieldReaders.Text(search, LegalNatureLabels, minimumLength: 3)),
            ["street"] = search.Field("street", () => FieldReaders.Text(search, StreetLabels, minimumLength: 3)),
            ["number"] = search.Field("number", () => FieldReaders.Text(search, NumberLabels, minimumLength: 1)),
            ["complement"] = search.Field("complement", () => FieldReaders.Text(search, ComplementLabels, minimumLength: 1)),
            ["postalCode"] = search.Field("postalCode", () => FieldReaders.PostalCode(search, PostalCodeLabels)),
            ["neighborhood"] = search.Field("neighborhood", () => FieldReaders.Text(search, NeighborhoodLabels, minimumLength: 2)),
            ["city"] = search.Field("city", () => FieldReaders.Text(search, CityLabels, minimumLength: 2)),
            ["state"] = search.Field("state", () => FieldReaders.State(search, StateLabels)),
            ["registrationStatus"] = search.Field("registrationStatus", () => FieldReaders.Text(search, StatusLabels, minimumLength: 3)),
            ["registrationStatusDate"] = search.Field("registrationStatusDate", () => FieldReaders.Date(search, StatusDateLabels, DateKind.Issue, today))
        };

        return Task.FromResult(new StructuredExtraction(
            TypeName, SchemaVersion, FieldFactory.OverallConfidence(fields.Values), fields));
    }
}
