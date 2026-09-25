using System.Text.RegularExpressions;

namespace DocReader.Application.Extraction;

/// <summary>
/// Expressões regulares dos valores que aparecem em mais de um tipo documental. A fronteira de dígito
/// e letra evita casar um pedaço de número maior, e o separador tolerante absorve o que o OCR mistura.
/// </summary>
internal static partial class ExtractionPatterns
{
    /// <summary>CPF com ou sem máscara.</summary>
    [GeneratedRegex(@"(?<!\d)\d{3}\.?\s?\d{3}\.?\s?\d{3}\s?-?\s?\d{2}(?!\d)", RegexOptions.CultureInvariant)]
    public static partial Regex Cpf();

    /// <summary>
    /// CNPJ mascarado, que pode ter letras nas 12 primeiras posições, ou CNPJ só de 14 dígitos sem máscara.
    /// Sem máscara, letras não são aceitas: qualquer palavra de 14 letras e números casaria.
    /// </summary>
    [GeneratedRegex(
        @"(?<![0-9A-Za-z])(?:[0-9A-Za-z]{2}\.\s?[0-9A-Za-z]{3}\.\s?[0-9A-Za-z]{3}\s?/\s?[0-9A-Za-z]{4}\s?-\s?\d{2}|\d{14})(?![0-9A-Za-z])",
        RegexOptions.CultureInvariant)]
    public static partial Regex Cnpj();

    /// <summary>Data dia/mês/ano com separador tolerante ao OCR.</summary>
    [GeneratedRegex(@"(?<!\d)\d{1,2}[/.\-]\d{1,2}[/.\-]\d{4}(?!\d)", RegexOptions.CultureInvariant)]
    public static partial Regex Date();

    /// <summary>
    /// Data impressa com espaço no lugar da barra, "12 07 1975", como sai de alguns RGs. Só vale como reserva, perto
    /// de um rótulo de data: sem o rótulo, três números separados por espaço são qualquer coisa.
    /// </summary>
    [GeneratedRegex(@"(?<!\d)\d{1,2} {1,2}\d{1,2} {1,2}\d{4}(?!\d)", RegexOptions.CultureInvariant)]
    public static partial Regex DateSpaced();

    /// <summary>Data por extenso, "24 de setembro de 2026", em qualquer caixa e com ou sem acento.</summary>
    [GeneratedRegex(@"(?<!\d)\d{1,2}\s+DE\s+[A-Z]{4,9}\s+DE\s+\d{4}(?!\d)", RegexOptions.CultureInvariant)]
    public static partial Regex LongDate();

    /// <summary>Competência de fatura, "09/2026".</summary>
    [GeneratedRegex(@"(?<!\d)\d{1,2}[/.\-]\d{4}(?!\d)", RegexOptions.CultureInvariant)]
    public static partial Regex MonthYear();

    /// <summary>CEP com ou sem máscara.</summary>
    [GeneratedRegex(@"(?<!\d)\d{2}\.?\d{3}\s?-?\s?\d{3}(?!\d)", RegexOptions.CultureInvariant)]
    public static partial Regex PostalCode();

    /// <summary>Valor em reais, com ou sem o símbolo.</summary>
    [GeneratedRegex(@"(?:R\$\s?)?\d{1,3}(?:\.\d{3})*,\d{2}(?!\d)", RegexOptions.CultureInvariant)]
    public static partial Regex Money();

    /// <summary>Código da CNAE, "62.01-5-01".</summary>
    [GeneratedRegex(@"(?<!\d)\d{2}\.\d{2}-\d(?:-\d{2})?(?!\d)", RegexOptions.CultureInvariant)]
    public static partial Regex Cnae();

    /// <summary>Início de logradouro, já sem acento e em caixa alta.</summary>
    [GeneratedRegex(
        @"^(RUA|AV|AVENIDA|ALAMEDA|AL|TRAVESSA|TV|PRACA|PCA|RODOVIA|ROD|ESTRADA|EST|LARGO|VIA|VIELA|QUADRA|SQN|SQS|SHIN|SHIS)\b\.?",
        RegexOptions.CultureInvariant)]
    public static partial Regex StreetStart();

    /// <summary>Formatos que sozinhos já indicam um endereço numa linha: "SAO PAULO - SP", "SAO PAULO/SP".</summary>
    [GeneratedRegex(@"(?<city>[A-Z][A-Z' ]{2,60}?)\s*[-/,]\s*(?<uf>[A-Z]{2})\b", RegexOptions.CultureInvariant)]
    public static partial Regex CityAndState();
}
