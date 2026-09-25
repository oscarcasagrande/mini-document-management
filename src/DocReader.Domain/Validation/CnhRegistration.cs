namespace DocReader.Domain.Validation;

/// <summary>
/// Normalização e validação do número de registro da CNH: 11 dígitos, os 9 primeiros de base e os 2
/// últimos verificadores.
///
/// O algoritmo é o do DENATRAN, na forma em que as bibliotecas de validação o reproduzem
/// (github.com/paemuri/brdoc, <c>IsCNH</c>):
/// <list type="number">
/// <item>1º dígito: soma dos 9 dígitos de base com pesos 9, 8, …, 1; o resto da divisão por 11 é o dígito, e
/// resto 10 vira 0 e ativa um desconto de 2 no segundo dígito.</item>
/// <item>2º dígito: soma dos mesmos 9 dígitos com pesos 1, 2, …, 9; resto por 11 menos o desconto, somando 11
/// se ficar negativo; resultado acima de 9 vira 0.</item>
/// </list>
/// Sequência de um dígito repetido é rejeitada mesmo quando a conta fecha, como no CPF.
///
/// A PoC não tem acesso ao DENATRAN: a regra é a publicada e a que os validadores usam, não uma conferência
/// contra a base oficial.
/// </summary>
public static class CnhRegistration
{
    public const int Length = 11;

    private const int BaseLength = 9;

    /// <summary>Devolve os 11 dígitos, sem separadores. Falha quando não há exatamente 11 dígitos.</summary>
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

        normalized = new string(digits);
        return true;
    }

    /// <summary>Confere os dois dígitos verificadores.</summary>
    public static bool IsValid(string? value)
    {
        if (!TryNormalize(value, out var normalized) || HasSingleRepeatedDigit(normalized))
        {
            return false;
        }

        return CheckDigits(normalized[..BaseLength]) == normalized[BaseLength..];
    }

    /// <summary>Os dois dígitos verificadores dos 9 dígitos de base, como texto de dois caracteres.</summary>
    public static string CheckDigits(string baseDigits)
    {
        if (baseDigits.Length != BaseLength || !baseDigits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException($"A base precisa ter {BaseLength} dígitos.", nameof(baseDigits));
        }

        var descending = 0;
        var ascending = 0;

        for (var index = 0; index < BaseLength; index++)
        {
            var digit = baseDigits[index] - '0';
            descending += digit * (BaseLength - index);
            ascending += digit * (index + 1);
        }

        var first = descending % 11;
        var discount = 0;
        if (first == 10)
        {
            discount = 2;
        }

        if (first > 9)
        {
            first = 0;
        }

        var second = (ascending % 11) - discount;
        if (second < 0)
        {
            second += 11;
        }

        if (second > 9)
        {
            second = 0;
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{first}{second}");
    }

    private static bool HasSingleRepeatedDigit(string digits)
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
}
