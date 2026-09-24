using System.Globalization;

namespace DocReader.Domain.Validation;

/// <summary>
/// Normalização e validação de CPF, conforme RF-012 do PRD.
///
/// O CPF tem 11 dígitos: 9 de base e 2 verificadores, calculados por módulo 11 com pesos
/// decrescentes. Diferente do CNPJ, ele não admite letras.
/// </summary>
public static class Cpf
{
    public const int Length = 11;

    private const int BaseLength = 9;

    /// <summary>
    /// Remove máscara e qualquer caractere não numérico. Devolve false quando o que sobra não tem
    /// exatamente 11 dígitos, de modo que lixo de OCR não vira CPF por acidente.
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
                // Só separadores de máscara e espaços podem aparecer entre os dígitos.
                if (character is '.' or '-' or '/' or ' ' or '\t')
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

        normalized = new string(digits);
        return true;
    }

    /// <summary>
    /// Valida um CPF já normalizado ou mascarado. Sequências de dígito repetido são rejeitadas
    /// mesmo quando o módulo 11 fecha, porque são o caso clássico de falso positivo.
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (!TryNormalize(value, out var normalized))
        {
            return false;
        }

        if (HasSingleRepeatedDigit(normalized))
        {
            return false;
        }

        var first = CheckDigit(normalized, BaseLength);
        if (normalized[BaseLength] - '0' != first)
        {
            return false;
        }

        var second = CheckDigit(normalized, BaseLength + 1);
        return normalized[BaseLength + 1] - '0' == second;
    }

    /// <summary>
    /// Calcula o dígito verificador da posição indicada. Os pesos começam em
    /// <paramref name="length"/> + 1 e decrescem até 2.
    /// </summary>
    private static int CheckDigit(ReadOnlySpan<char> digits, int length)
    {
        var sum = 0;
        var weight = length + 1;

        for (var index = 0; index < length; index++)
        {
            sum += (digits[index] - '0') * weight;
            weight--;
        }

        var remainder = sum * 10 % 11;

        return remainder == 10 ? 0 : remainder;
    }

    private static bool HasSingleRepeatedDigit(ReadOnlySpan<char> digits)
    {
        for (var index = 1; index < digits.Length; index++)
        {
            if (digits[index] != digits[0])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Aplica a máscara <c>000.000.000-00</c> a um valor já normalizado.</summary>
    public static string Format(string normalized)
    {
        if (normalized.Length != Length)
        {
            throw new ArgumentException($"CPF normalizado precisa ter {Length} dígitos.", nameof(normalized));
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{normalized[..3]}.{normalized[3..6]}.{normalized[6..9]}-{normalized[9..]}");
    }
}
