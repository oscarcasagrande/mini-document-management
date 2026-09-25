using System.Globalization;

namespace DocReader.Domain.Validation;

/// <summary>
/// Normalização de CEP, conforme RF-012 do PRD. O CEP tem oito dígitos e não tem dígito verificador,
/// então a validação é de formato.
/// </summary>
public static class PostalCode
{
    public const int Length = 8;

    /// <summary>
    /// Aceita <c>01000-000</c>, <c>01.000-000</c> e <c>01000000</c>. Devolve false quando o que sobra
    /// não tem oito dígitos ou é tudo zero.
    /// </summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        Span<char> digits = stackalloc char[Length];
        var count = 0;

        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                if (character is '.' or '-' or ' ' or '\t')
                {
                    continue;
                }

                return false;
            }

            if (count == Length)
            {
                return false;
            }

            digits[count++] = character;
        }

        if (count != Length)
        {
            return false;
        }

        var candidate = new string(digits);
        if (candidate.All(digit => digit == '0'))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    /// <summary>Aplica a máscara <c>00000-000</c> a um valor já normalizado.</summary>
    public static string Format(string normalized)
    {
        if (normalized.Length != Length)
        {
            throw new ArgumentException($"CEP normalizado precisa ter {Length} dígitos.", nameof(normalized));
        }

        return string.Create(CultureInfo.InvariantCulture, $"{normalized[..5]}-{normalized[5..]}");
    }
}
