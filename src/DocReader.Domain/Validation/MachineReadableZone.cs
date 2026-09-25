using System.Globalization;

namespace DocReader.Domain.Validation;

/// <summary>
/// Zona de leitura mecânica no formato TD1 do ICAO 9303: três linhas de 30 caracteres, como a de um
/// cartão de identidade. Só o que a PoC valida: os dígitos verificadores e as datas.
/// </summary>
public static class MachineReadableZone
{
    public const int LineLength = 30;

    public const int LineCount = 3;

    private static readonly int[] Weights = [7, 3, 1];

    /// <summary>
    /// Dígito verificador do ICAO 9303: <c>0</c>–<c>9</c> valem 0 a 9, <c>A</c>–<c>Z</c> valem 10 a 35 e o
    /// preenchimento <c>&lt;</c> vale 0; pesos 7, 3, 1 repetidos; módulo 10. Devolve -1 para caractere fora
    /// desse alfabeto.
    /// </summary>
    public static int CheckDigit(string field)
    {
        var sum = 0;

        for (var index = 0; index < field.Length; index++)
        {
            var value = CharacterValue(field[index]);
            if (value < 0)
            {
                return -1;
            }

            sum += value * Weights[index % Weights.Length];
        }

        return sum % 10;
    }

    /// <summary>
    /// Interpreta as três linhas. Devolve false quando não são três linhas de 30 caracteres do
    /// alfabeto da MRZ; dígito verificador que não confere não impede a leitura, fica registrado no
    /// resultado, porque o operador precisa ver o que o OCR leu.
    /// </summary>
    public static bool TryParseTd1(IReadOnlyList<string> lines, DateOnly today, out MrzReading reading)
    {
        reading = default!;

        if (lines.Count != LineCount || lines.Any(line => line.Length != LineLength || !line.All(IsMrzCharacter)))
        {
            return false;
        }

        var line1 = lines[0];
        var line2 = lines[1];
        var line3 = lines[2];

        var documentNumber = line1[5..14];
        var birth = line2[..6];
        var expiry = line2[8..14];

        var compositeSource = line1[5..30] + line2[..7] + line2[8..15] + line2[18..29];

        reading = new MrzReading(
            DocumentCode: line1[..2].TrimEnd('<'),
            IssuingState: line1[2..5],
            DocumentNumber: documentNumber.TrimEnd('<'),
            BirthDate: ParseDate(birth, today, birthDate: true),
            ExpirationDate: ParseDate(expiry, today, birthDate: false),
            Nationality: line2[15..18],
            Names: line3.Replace("<<", " / ", StringComparison.Ordinal).Replace('<', ' ').Trim(),
            DocumentNumberCheckValid: DigitMatches(documentNumber, line1[14]),
            BirthDateCheckValid: DigitMatches(birth, line2[6]),
            ExpirationDateCheckValid: DigitMatches(expiry, line2[14]),
            CompositeCheckValid: DigitMatches(compositeSource, line2[29]));

        return true;
    }

    private static bool DigitMatches(string field, char digit)
    {
        var expected = CheckDigit(field);
        return expected >= 0 && char.IsAsciiDigit(digit) && digit - '0' == expected;
    }

    private static int CharacterValue(char character) => character switch
    {
        '<' => 0,
        >= '0' and <= '9' => character - '0',
        >= 'A' and <= 'Z' => character - 'A' + 10,
        _ => -1
    };

    private static bool IsMrzCharacter(char character) => CharacterValue(character) >= 0;

    /// <summary>
    /// A MRZ traz ano de dois dígitos. Nascimento no futuro é do século passado; a validade fica no
    /// século atual.
    /// </summary>
    private static DateOnly? ParseDate(string yymmdd, DateOnly today, bool birthDate)
    {
        if (!yymmdd.All(char.IsAsciiDigit))
        {
            return null;
        }

        var yy = int.Parse(yymmdd[..2], CultureInfo.InvariantCulture);
        var month = int.Parse(yymmdd[2..4], CultureInfo.InvariantCulture);
        var day = int.Parse(yymmdd[4..6], CultureInfo.InvariantCulture);

        var century = today.Year / 100 * 100;
        var year = century + yy;

        if (birthDate && year > today.Year)
        {
            year -= 100;
        }

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        return new DateOnly(year, month, day);
    }
}

/// <summary>Campos de uma MRZ TD1 e o resultado de cada dígito verificador.</summary>
public sealed record MrzReading(
    string DocumentCode,
    string IssuingState,
    string DocumentNumber,
    DateOnly? BirthDate,
    DateOnly? ExpirationDate,
    string Nationality,
    string Names,
    bool DocumentNumberCheckValid,
    bool BirthDateCheckValid,
    bool ExpirationDateCheckValid,
    bool CompositeCheckValid)
{
    /// <summary>Todos os dígitos verificadores conferem e as duas datas existem.</summary>
    public bool IsFullyValid =>
        DocumentNumberCheckValid && BirthDateCheckValid && ExpirationDateCheckValid && CompositeCheckValid &&
        BirthDate is not null && ExpirationDate is not null;
}
