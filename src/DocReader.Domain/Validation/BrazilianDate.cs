using System.Globalization;

namespace DocReader.Domain.Validation;

/// <summary>
/// Normalização de datas lidas de documentos brasileiros para ISO 8601, conforme RF-012.
///
/// O OCR costuma confundir os separadores, então <c>14/03/1985</c>, <c>14-03-1985</c> e
/// <c>14.03.1985</c> são aceitos. O que não se aceita é ambiguidade: só o formato dia/mês/ano é
/// reconhecido, porque em documento brasileiro é o único que aparece.
/// </summary>
public static class BrazilianDate
{
    /// <summary>Data mais antiga considerada plausível em um documento de identificação.</summary>
    public static readonly DateOnly MinimumPlausible = new(1900, 1, 1);

    private static readonly char[] Separators = ['/', '-', '.', ' '];

    /// <summary>
    /// Converte para <see cref="DateOnly"/>. Devolve false para data impossível, como 31/02, e para
    /// qualquer coisa fora do formato dia/mês/ano com quatro dígitos de ano.
    /// </summary>
    public static bool TryParse(string? value, out DateOnly parsed)
    {
        parsed = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        if (parts[0].Length is < 1 or > 2 || parts[1].Length is < 1 or > 2 || parts[2].Length != 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var day) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            return false;
        }

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        parsed = new DateOnly(year, month, day);
        return true;
    }

    /// <summary>
    /// Verifica se a data é plausível como data de nascimento: existe, não é futura e não é anterior
    /// a 1900. <paramref name="today"/> vem do chamador para manter a função determinística.
    /// </summary>
    public static bool IsPlausibleBirthDate(DateOnly value, DateOnly today) =>
        value >= MinimumPlausible && value <= today;

    /// <summary>Formato de persistência e de resposta da API.</summary>
    public static string ToIso(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
