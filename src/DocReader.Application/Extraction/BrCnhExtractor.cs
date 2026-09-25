using System.Text.RegularExpressions;
using DocReader.Application.Abstractions;
using DocReader.Domain.Validation;

namespace DocReader.Application.Extraction;

/// <summary>
/// Extrator da Carteira Nacional de Habilitação, conforme <c>schemas/documents/BR_CNH.v1.json</c>.
///
/// O CPF e o número de registro passam pelo dígito verificador (`CnhRegistration`). A validade vencida não
/// reprova o campo, que continua sendo uma data lida corretamente: ela é sinalizada com
/// <see cref="FieldReaders.DocumentExpired"/>.
/// </summary>
public sealed partial class BrCnhExtractor(TimeProvider timeProvider) : IDocumentExtractor
{
    public const string TypeName = "BR_CNH";

    public const string ExtractorVersion = "br-cnh-1.1.0";

    public const string CategoryValid = "CATEGORY_VALID";

    private static readonly string[] NameLabels = ["NOME", "NOME E SOBRENOME"];
    private static readonly string[] CpfLabels = ["CPF"];
    private static readonly string[] BirthLabels = ["DATA DE NASCIMENTO", "DATA NASCIMENTO", "DATA NASC", "NASCIMENTO", "DATA, LOCAL E UF DE NASCIMENTO"];
    private static readonly string[] RegistrationLabels = ["N REGISTRO", "Nº REGISTRO", "N° REGISTRO", "NO REGISTRO", "NUMERO DE REGISTRO", "N DE REGISTRO", "REGISTRO"];
    private static readonly string[] CategoryLabels = ["CAT. HAB.", "CAT. HAB", "CAT HAB", "CATEGORIA", "CATEGORIA DE HABILITACAO"];
    private static readonly string[] FirstLicenseLabels = ["1A HABILITACAO", "1ª HABILITACAO", "PRIMEIRA HABILITACAO", "1 HABILITACAO", "1A HAB"];
    private static readonly string[] IssueLabels = ["DATA EMISSAO", "DATA DE EMISSAO", "EMISSAO"];
    private static readonly string[] ExpirationLabels = ["VALIDADE", "DATA DE VALIDADE"];

    private static readonly string[] KnownLabels =
    [
        .. NameLabels, .. CpfLabels, .. BirthLabels, .. RegistrationLabels, .. CategoryLabels,
        .. FirstLicenseLabels, .. IssueLabels, .. ExpirationLabels,
        "FILIACAO", "DOC. IDENTIDADE / ORG. EMISSOR / UF", "DOC IDENTIDADE", "ORGAO EMISSOR", "PERMISSAO",
        "OBSERVACOES", "LOCAL", "NACIONALIDADE", "ASSINATURA DO PORTADOR", "ASSINATURA DO EMISSOR", "CHANCELA", "REPUBLICA FEDERATIVA DO BRASIL",
        "MINISTERIO DOS TRANSPORTES", "SECRETARIA NACIONAL DE TRANSITO", "CARTEIRA NACIONAL DE HABILITACAO",
        "AMOSTRA SINTETICA - SEM VALOR LEGAL", "AMOSTRA SINTETICA SEM VALOR LEGAL"
    ];

    /// <summary>Categorias que a CNH pode trazer, sozinhas ou combinadas.</summary>
    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    {
        "A", "B", "C", "D", "E", "AB", "AC", "AD", "AE", "ACC"
    };

    public string DocumentType => TypeName;

    public int SchemaVersion => 1;

    public string Version => ExtractorVersion;

    public Task<StructuredExtraction> ExtractAsync(OcrResult result, CancellationToken ct) => ExtractAsync(result, null, ct);

    public Task<StructuredExtraction> ExtractAsync(OcrResult result, ExtractionTrace? trace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        var search = new LineSearch(OcrTextLine.From(result), KnownLabels, trace);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        var fields = new Dictionary<string, ExtractedFieldValue>(StringComparer.Ordinal)
        {
            ["name"] = search.Field("name", () => FieldReaders.Name(search, NameLabels)),
            ["cpf"] = search.Field("cpf", () => FieldReaders.Cpf(search, CpfLabels)),
            ["birthDate"] = search.Field("birthDate", () => FieldReaders.Date(search, BirthLabels, DateKind.Birth, today)),
            ["registrationNumber"] = search.Field("registrationNumber", () => ExtractRegistration(search)),
            ["category"] = search.Field("category", () => ExtractCategory(search)),
            ["firstLicenseDate"] = search.Field("firstLicenseDate", () => FieldReaders.Date(search, FirstLicenseLabels, DateKind.Issue, today)),
            ["issueDate"] = search.Field("issueDate", () => FieldReaders.Date(search, IssueLabels, DateKind.Issue, today)),
            ["expirationDate"] = search.Field("expirationDate", () => FieldReaders.Date(search, ExpirationLabels, DateKind.Expiration, today))
        };

        return Task.FromResult(new StructuredExtraction(
            TypeName, SchemaVersion, FieldFactory.OverallConfidence(fields.Values), fields));
    }

    /// <summary>
    /// Um registro com dígitos verificadores certos vence qualquer outro candidato; se só houver os que
    /// reprovam, o primeiro sai como INVALID com o valor lido preservado, como no CPF.
    /// </summary>
    private static ExtractedFieldValue ExtractRegistration(LineSearch search)
    {
        LabelledValue? rejected = null;
        string? rejectedRaw = null;

        foreach (var candidate in search.After(RegistrationLabels))
        {
            var match = RegistrationPattern().Match(candidate.Text);
            if (!match.Success)
            {
                continue;
            }

            if (CnhRegistration.TryNormalize(match.Value, out var normalized) && CnhRegistration.IsValid(normalized))
            {
                return FieldFactory.Found(match.Value, normalized, candidate.Line, candidate.Penalty, FieldValidationStatus.Valid, FieldReaders.CheckDigitValid);
            }

            rejected ??= candidate;
            rejectedRaw ??= match.Value;
        }

        return rejected is null
            ? FieldFactory.NotFound()
            : FieldFactory.Build(
                rejectedRaw!, null, rejected.Line, FieldFactory.FallbackConfidencePenalty + rejected.Penalty,
                FieldValidationStatus.Invalid, [FieldReaders.CheckDigitInvalid]);
    }

    private static ExtractedFieldValue ExtractCategory(LineSearch search)
    {
        foreach (var candidate in search.After(CategoryLabels))
        {
            var normalized = new string([.. TextNormalization.ForMatching(candidate.Text).Where(char.IsAsciiLetterUpper)]);
            if (Categories.Contains(normalized))
            {
                return FieldFactory.Found(candidate.Text.Trim(), normalized, candidate.Line, candidate.Penalty, FieldValidationStatus.Valid, CategoryValid);
            }
        }

        return FieldFactory.NotFound();
    }

    /// <summary>Número de registro: 11 dígitos, sem separador.</summary>
    [GeneratedRegex(@"(?<!\d)\d{11}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex RegistrationPattern();
}
