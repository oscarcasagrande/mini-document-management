using System.Globalization;
using System.Text;

namespace DocReader.Domain.Validation;

/// <summary>
/// Normalização e validação de CNPJ, conforme RF-012 do PRD.
///
/// O formato alfanumérico é a regra geral e o numérico legado é um caso particular dela: as 12
/// primeiras posições aceitam <c>0</c>–<c>9</c> e <c>A</c>–<c>Z</c>, e as duas últimas são os dígitos
/// verificadores, sempre numéricos. Não existe um segundo algoritmo para o CNPJ só de números.
/// </summary>
public static class Cnpj
{
    public const int Length = 14;

    private const int BaseLength = 12;

    private static readonly int[] FirstWeights = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];

    private static readonly int[] SecondWeights = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];

    /// <summary>
    /// Remove a máscara, converte para maiúsculas e tira a acentuação. Devolve false quando o que sobra
    /// não tem 14 posições, quando há caractere fora de <c>[0-9A-Z]</c> nas 12 primeiras ou fora de
    /// <c>[0-9]</c> nos dois dígitos verificadores.
    /// </summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var builder = new StringBuilder(Length);

        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            // Só separadores de máscara e espaços podem aparecer entre os caracteres.
            if (character is '.' or '-' or '/' or ' ' or '\t')
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(character));
        }

        if (builder.Length != Length)
        {
            return false;
        }

        for (var index = 0; index < Length; index++)
        {
            var character = builder[index];
            var allowed = index < BaseLength
                ? char.IsAsciiDigit(character) || char.IsAsciiLetterUpper(character)
                : char.IsAsciiDigit(character);

            if (!allowed)
            {
                return false;
            }
        }

        normalized = builder.ToString();
        return true;
    }

    /// <summary>
    /// Valida os dígitos verificadores. As 12 primeiras posições todas iguais são rejeitadas mesmo
    /// quando o módulo 11 fecha, porque é o caso clássico de falso positivo.
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (!TryNormalize(value, out var normalized))
        {
            return false;
        }

        if (HasSingleRepeatedBase(normalized))
        {
            return false;
        }

        var first = CheckDigit(normalized, BaseLength, FirstWeights);
        if (normalized[BaseLength] - '0' != first)
        {
            return false;
        }

        var second = CheckDigit(normalized, BaseLength + 1, SecondWeights);
        return normalized[BaseLength + 1] - '0' == second;
    }

    /// <summary>Aplica a máscara <c>XX.XXX.XXX/XXXX-DD</c> a um valor já normalizado.</summary>
    public static string Format(string normalized)
    {
        if (normalized.Length != Length)
        {
            throw new ArgumentException($"CNPJ normalizado precisa ter {Length} posições.", nameof(normalized));
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{normalized[..2]}.{normalized[2..5]}.{normalized[5..8]}/{normalized[8..12]}-{normalized[12..]}");
    }

    /// <summary>
    /// Cada posição vale o código ASCII menos 48: <c>0</c>–<c>9</c> valem 0 a 9 e <c>A</c>–<c>Z</c> valem
    /// 17 a 42. Módulo 11 com os pesos da posição; resto 0 ou 1 dá dígito 0, senão 11 menos o resto.
    /// </summary>
    private static int CheckDigit(string characters, int length, int[] weights)
    {
        var sum = 0;

        for (var index = 0; index < length; index++)
        {
            sum += (characters[index] - '0') * weights[index];
        }

        var remainder = sum % 11;

        return remainder < 2 ? 0 : 11 - remainder;
    }

    private static bool HasSingleRepeatedBase(string normalized)
    {
        for (var index = 1; index < BaseLength; index++)
        {
            if (normalized[index] != normalized[0])
            {
                return false;
            }
        }

        return true;
    }
}
