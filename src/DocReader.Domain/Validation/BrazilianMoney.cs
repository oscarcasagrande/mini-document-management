using System.Globalization;

namespace DocReader.Domain.Validation;

/// <summary>
/// Valor monetário em reais lido de um documento: <c>R$ 4.780,00</c>, <c>4.780,00</c> ou
/// <c>4780,00</c>. O ponto é separador de milhar e a vírgula é o decimal, o contrário do formato
/// invariante, então a conversão é explícita em vez de depender de cultura.
/// </summary>
public static class BrazilianMoney
{
    /// <summary>Converte para decimal. Devolve false para qualquer texto que não seja um valor em reais.</summary>
    public static bool TryParse(string? value, out decimal amount)
    {
        amount = 0m;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith("R$", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..].Trim();
        }

        if (text.Length == 0 || !text.All(character => char.IsAsciiDigit(character) || character is '.' or ','))
        {
            return false;
        }

        // Vírgula decimal opcional, com exatamente duas casas; o restante é a parte inteira.
        var comma = text.LastIndexOf(',');
        var integerPart = comma < 0 ? text : text[..comma];
        var fraction = comma < 0 ? "00" : text[(comma + 1)..];

        if (fraction.Length != 2 || !fraction.All(char.IsAsciiDigit) || integerPart.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var groups = integerPart.Split('.');
        if (groups[0].Length is 0 or > 3 && groups.Length > 1)
        {
            return false;
        }

        if (groups.Skip(1).Any(group => group.Length != 3))
        {
            return false;
        }

        var digits = string.Concat(groups);
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        return decimal.TryParse(
            $"{digits}.{fraction}",
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out amount);
    }

    /// <summary>Formato de persistência e de resposta: ponto decimal, duas casas, sem separador de milhar.</summary>
    public static string ToInvariant(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);
}
