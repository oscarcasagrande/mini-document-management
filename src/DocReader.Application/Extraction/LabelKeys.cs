using System.Text;
using System.Text.RegularExpressions;

namespace DocReader.Application.Extraction;

/// <summary>
/// As formas em que uma linha de OCR pode ser um rótulo, para comparar sem depender de espaço, pontuação
/// ou do que o documento imprime ao lado. O texto já vem sem acento e em caixa alta.
///
/// - só letras e dígitos: o OCR cola palavras ("5N°REGISTRO") e troca "Nº", "1ª", "1°" por sinais diferentes;
/// - sem o numerador de campo: "4d CPF", "2e 1 NOME E SOBRENOME", "9 CAT. HAB.";
/// - cada lado de um rótulo bilíngue: "NOME/NAME", "RG/UF", "DATA DE VALIDADE / EXPIRY".
/// </summary>
internal static partial class LabelKeys
{
    private const int MinimumStrippedLength = 3;

    /// <summary>Letras e dígitos do texto, na mesma ordem.</summary>
    public static string Compact(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    public static IReadOnlyList<string> From(string normalized)
    {
        var keys = new List<string>();
        Add(keys, normalized);

        if (normalized.Contains('/', StringComparison.Ordinal))
        {
            var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                foreach (var part in parts)
                {
                    Add(keys, part);
                }
            }
        }

        return keys;
    }

    private static void Add(List<string> keys, string text)
    {
        var compact = Compact(text);
        if (compact.Length == 0)
        {
            return;
        }

        AddDistinct(keys, compact);

        foreach (var enumerator in EnumeratorSet)
        {
            var match = enumerator.Match(compact);
            if (match.Success && compact.Length - match.Length >= MinimumStrippedLength)
            {
                AddDistinct(keys, compact[match.Length..]);
            }
        }
    }

    private static void AddDistinct(List<string> keys, string key)
    {
        if (!keys.Contains(key))
        {
            keys.Add(key);
        }
    }

    /// <summary>"4", "4B", "2E1": dígitos, com uma letra e mais dígitos depois, que numeram o campo.</summary>
    private static readonly Regex[] EnumeratorSet =
    [
        DigitsOnly(),
        DigitsAndLetter(),
        DigitsLetterDigits()
    ];

    [GeneratedRegex(@"^\d{1,2}", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsOnly();

    [GeneratedRegex(@"^\d{1,2}[A-Z]", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsAndLetter();

    [GeneratedRegex(@"^\d{1,2}[A-Z]\d{1,2}", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsLetterDigits();
}
