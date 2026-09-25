using System.Text.RegularExpressions;
using DocReader.Application.Abstractions;
using DocReader.Domain.Validation;

namespace DocReader.Application.Extraction;

/// <summary>
/// Extrator da CIN e do RG, conforme <c>schemas/documents/BR_CIN.v1.json</c>. Os dois documentos têm os
/// mesmos campos com rótulos quase iguais, então um extrator só atende os dois; o layout antigo do RG
/// (sem MRZ, com "REGISTRO GERAL") e o da CIN (com CPF como número e MRZ no verso) caem nas mesmas regras.
///
/// A MRZ, quando o OCR a lê em três linhas de 30 caracteres, é validada pelos dígitos verificadores do
/// ICAO 9303 e conferida contra a data de nascimento impressa.
/// </summary>
public sealed partial class BrCinExtractor(TimeProvider timeProvider) : IDocumentExtractor
{
    public const string TypeName = "BR_CIN";

    public const string ExtractorVersion = "br-cin-1.1.0";

    public const string MrzCheckValid = "MRZ_CHECK_VALID";
    public const string MrzCheckInvalid = "MRZ_CHECK_INVALID";
    public const string MrzBirthDateMatch = "MRZ_BIRTH_DATE_MATCH";
    public const string MrzBirthDateMismatch = "MRZ_BIRTH_DATE_MISMATCH";
    public const string MrzLengthUnexpected = "MRZ_LENGTH_UNEXPECTED";
    public const string OrderAssumed = "FILIATION_ORDER_ASSUMED";

    private static readonly string[] NameLabels = ["NOME", "NOME COMPLETO"];
    private static readonly string[] CpfLabels = ["CPF", "N DO CPF", "NUMERO DO CPF", "NUMERO CPF"];
    private static readonly string[] RgLabels = ["REGISTRO GERAL", "REGISTRO GERAL (RG)", "N DO REGISTRO GERAL", "RG"];
    private static readonly string[] BirthLabels = ["DATA DE NASCIMENTO", "DATA NASCIMENTO", "DATA DE NASC", "DATA NASC", "NASCIMENTO"];
    private static readonly string[] IssueLabels = ["DATA DE EXPEDICAO", "DATA DA EXPEDICAO", "DATA EXPEDICAO", "EXPEDICAO", "DATA DE EMISSAO", "DATA DA EMISSAO", "DATA EMISSAO"];
    private static readonly string[] ExpirationLabels = ["DATA DE VALIDADE", "VALIDADE"];
    private static readonly string[] BirthPlaceLabels = ["NATURALIDADE"];
    private static readonly string[] FatherLabels = ["NOME DO PAI", "PAI"];
    private static readonly string[] MotherLabels = ["NOME DA MAE", "MAE"];
    private static readonly string[] FiliationLabels = ["FILIACAO"];

    /// <summary>Rótulos e cabeçalhos que nunca são valor de campo.</summary>
    private static readonly string[] KnownLabels =
    [
        .. NameLabels, .. CpfLabels, .. RgLabels, .. BirthLabels, .. IssueLabels, .. ExpirationLabels,
        .. BirthPlaceLabels, .. FatherLabels, .. MotherLabels, .. FiliationLabels,
        "NOME SOCIAL", "SEXO", "NACIONALIDADE", "ORGAO EMISSOR", "DOC ORIGEM", "DOC. ORIGEM", "ASSINATURA DO TITULAR",
        "NUMERO RIC", "TITULO DE ELEITOR", "NIS", "NIE", "PIS PASEP", "LOCAL", "OBSERVACOES", "UF",
        "ASSINATURA", "IMPRESSAO DIGITAL", "REPUBLICA FEDERATIVA DO BRASIL", "CARTEIRA DE IDENTIDADE",
        "CARTEIRA DE IDENTIDADE NACIONAL", "MINISTERIO DA JUSTICA E SEGURANCA PUBLICA",
        "AMOSTRA SINTETICA - SEM VALOR LEGAL", "AMOSTRA SINTETICA SEM VALOR LEGAL", "FRENTE", "VERSO"
    ];

    public string DocumentType => TypeName;

    public int SchemaVersion => 1;

    public string Version => ExtractorVersion;

    public Task<StructuredExtraction> ExtractAsync(OcrResult result, CancellationToken ct) => ExtractAsync(result, null, ct);

    public Task<StructuredExtraction> ExtractAsync(OcrResult result, ExtractionTrace? trace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lines = OcrTextLine.From(result);
        var search = new LineSearch(lines, KnownLabels, trace);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        var birthDate = search.Field("birthDate", () => FieldReaders.Date(search, BirthLabels, DateKind.Birth, today));
        var (father, mother) = search.Fields(["fatherName", "motherName"], () => ExtractParents(search));

        var fields = new Dictionary<string, ExtractedFieldValue>(StringComparer.Ordinal)
        {
            ["name"] = search.Field("name", () => FieldReaders.Name(search, NameLabels)),
            ["cpf"] = search.Field("cpf", () => FieldReaders.Cpf(search, CpfLabels)),
            ["rg"] = search.Field("rg", () => ExtractRg(search)),
            ["birthDate"] = birthDate,
            ["issueDate"] = search.Field("issueDate", () => FieldReaders.Date(search, IssueLabels, DateKind.Issue, today)),
            ["expirationDate"] = search.Field("expirationDate", () => FieldReaders.Date(search, ExpirationLabels, DateKind.Expiration, today)),
            ["birthPlace"] = search.Field("birthPlace", () => FieldReaders.Text(search, BirthPlaceLabels, minimumLength: 3)),
            ["fatherName"] = father,
            ["motherName"] = mother,
            ["mrz"] = ExtractMrz(lines, birthDate, today)
        };

        return Task.FromResult(new StructuredExtraction(
            TypeName, SchemaVersion, FieldFactory.OverallConfidence(fields.Values), fields));
    }

    /// <summary>
    /// Nome do pai e da mãe. Com rótulo próprio o valor é certo; com o bloco "FILIAÇÃO" só há duas linhas
    /// de nome e a ordem é a do costume (pai, depois mãe), o que o documento não garante: o campo sai
    /// UNCERTAIN, com o aviso, em vez de afirmar quem é quem.
    /// </summary>
    private static (ExtractedFieldValue Father, ExtractedFieldValue Mother) ExtractParents(LineSearch search)
    {
        var father = FieldReaders.Name(search, FatherLabels);
        var mother = FieldReaders.Name(search, MotherLabels);

        if (father.ValidationStatus != "NOT_FOUND" && mother.ValidationStatus != "NOT_FOUND")
        {
            return (father, mother);
        }

        var names = new List<(OcrTextLine Line, string Raw, string Cleaned)>();
        foreach (var label in search.LabelLines(FiliationLabels))
        {
            foreach (var line in search.Following(label, 3))
            {
                var cleaned = TextNormalization.CleanPersonName(line.Text);
                if (cleaned is not null)
                {
                    names.Add((line, line.Text.Trim(), cleaned));
                }
            }

            // Nome na mesma linha do rótulo, "FILIACAO: JOSE ..."
            if (label.Text.Contains(':', StringComparison.Ordinal))
            {
                var inline = label.Text[(label.Text.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
                var cleaned = TextNormalization.CleanPersonName(inline);
                if (cleaned is not null)
                {
                    names.Insert(0, (label, inline, cleaned));
                }
            }

            if (names.Count > 0)
            {
                break;
            }
        }

        if (father.ValidationStatus == "NOT_FOUND" && names.Count > 0)
        {
            var first = names[0];
            father = FieldFactory.Build(first.Raw, first.Cleaned, first.Line, 0.05m, FieldValidationStatus.Uncertain, [OrderAssumed]);
        }

        if (mother.ValidationStatus == "NOT_FOUND" && names.Count > 1)
        {
            var second = names[1];
            mother = FieldFactory.Build(second.Raw, second.Cleaned, second.Line, 0.05m, FieldValidationStatus.Uncertain, [OrderAssumed]);
        }

        return (father, mother);
    }

    /// <summary>
    /// Registro Geral: a numeração é de cada estado e não tem dígito verificador nacional, então a
    /// validação é de formato (sete a dez caracteres, o último podendo ser X).
    /// </summary>
    private static ExtractedFieldValue ExtractRg(LineSearch search)
    {
        foreach (var candidate in search.After(RgLabels))
        {
            var match = RgPattern().Match(candidate.Text);
            if (!match.Success)
            {
                continue;
            }

            var normalized = new string([.. match.Value.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).SkipWhile(char.IsAsciiLetter)]);
            if (normalized.Length is >= 7 and <= 10)
            {
                return FieldFactory.Found(match.Value, normalized, candidate.Line, candidate.Penalty, FieldValidationStatus.Valid, FieldReaders.FormatValid);
            }
        }

        return FieldFactory.NotFound();
    }

    private static ExtractedFieldValue ExtractMrz(IReadOnlyList<OcrTextLine> lines, ExtractedFieldValue birthDate, DateOnly today)
    {
        var block = FindMrzBlock(RowMerger.Merge(lines));
        if (block is null)
        {
            return FieldFactory.NotFound();
        }

        var cleaned = block.Select(line => RestoreDroppedFillers(CleanMrz(line.Text))).ToArray();
        var raw = string.Join('\n', block.Select(line => line.Text.Trim()));
        var weakest = block.OrderBy(line => line.Confidence ?? 1m).First();

        if (!MachineReadableZone.TryParseTd1(cleaned, today, out var reading))
        {
            return FieldFactory.Build(raw, null, weakest, 0.10m, FieldValidationStatus.Uncertain, [MrzLengthUnexpected]);
        }

        var messages = new List<string> { reading.IsFullyValid ? MrzCheckValid : MrzCheckInvalid };
        var status = reading.IsFullyValid ? FieldValidationStatus.Valid : FieldValidationStatus.Invalid;

        if (birthDate.ValidationStatus == "VALID" && reading.BirthDate is not null)
        {
            var matches = BrazilianDate.ToIso(reading.BirthDate.Value) == birthDate.Normalized;
            messages.Add(matches ? MrzBirthDateMatch : MrzBirthDateMismatch);

            if (!matches && status == FieldValidationStatus.Valid)
            {
                status = FieldValidationStatus.Uncertain;
            }
        }

        return FieldFactory.Build(raw, string.Join('\n', cleaned), weakest, 0m, status, [.. messages]);
    }

    /// <summary>Três linhas seguidas, cada uma com cara de MRZ: quase só maiúsculas, dígitos e "&lt;".</summary>
    private static IReadOnlyList<OcrTextLine>? FindMrzBlock(IReadOnlyList<OcrTextLine> lines)
    {
        for (var index = 0; index + 2 < lines.Count; index++)
        {
            var block = lines.Skip(index).Take(3).ToArray();
            if (block.All(line => line.PageNumber == block[0].PageNumber && LooksLikeMrz(line.Text)))
            {
                return block;
            }
        }

        return null;
    }

    private static bool LooksLikeMrz(string text)
    {
        var cleaned = CleanMrz(text);

        return cleaned.Length is >= 28 and <= 32
            && cleaned.Count(character => character == '<') >= 3
            && cleaned.All(character => char.IsAsciiDigit(character) || char.IsAsciiLetterUpper(character) || character == '<');
    }

    /// <summary>
    /// O OCR costuma perder alguns "&lt;" do fim de uma linha de preenchimento. Uma linha com um a três caracteres a
    /// menos que os 30 do TD1 e terminada em "&lt;" ganha os que faltam. Só o fim é reposto: os campos de posição vêm
    /// antes dele e os dígitos verificadores continuam decidindo se a leitura vale.
    /// </summary>
    private static string RestoreDroppedFillers(string line) =>
        line.Length is >= 27 and < 30 && line.EndsWith('<') ? line.PadRight(30, '<') : line;

    /// <summary>O OCR devolve o preenchimento como «, ‹ ou espaço; a MRZ só admite "&lt;".</summary>
    private static string CleanMrz(string text) =>
        text.ToUpperInvariant()
            .Replace('«', '<')
            .Replace('‹', '<')
            .Replace(" ", string.Empty, StringComparison.Ordinal);

    /// <summary>RG com ou sem máscara e com sigla de estado antes: "12.345.678-9", "MG-12.345.678".</summary>
    [GeneratedRegex(@"(?<![\dA-Za-z])(?:[A-Za-z]{2}-?)?\d{1,2}\.?\d{3}\.?\d{3}(?:-?[0-9Xx])?(?![\dA-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex RgPattern();
}
