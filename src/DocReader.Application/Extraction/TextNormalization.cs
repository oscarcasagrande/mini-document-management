using System.Globalization;
using System.Text;

namespace DocReader.Application.Extraction;

/// <summary>
/// Normalizações de texto usadas para casar rótulos em saída de OCR.
/// </summary>
public static class TextNormalization
{
    /// <summary>
    /// Forma canônica para comparação: sem acento, em caixa alta, com espaços colapsados. Documento
    /// brasileiro tem acento e o OCR erra acento com frequência, então rótulo nunca é comparado
    /// com o texto cru.
    /// </summary>
    public static string ForMatching(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var lastWasSpace = true;

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(char.ToUpperInvariant(character));
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Limpa um nome próprio lido por OCR: remove pontuação que não pertence a nome, colapsa
    /// espaços e devolve em caixa alta. Devolve null quando o que sobra não parece um nome.
    /// </summary>
    public static string? CleanPersonName(string? value)
    {
        var normalized = ForMatching(value);
        if (normalized.Length == 0)
        {
            return null;
        }

        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (char.IsAsciiLetter(character) || character is ' ' or '\'' or '-')
            {
                builder.Append(character);
            }
        }

        var cleaned = builder.ToString().Trim();
        while (cleaned.Contains("  ", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        }

        return LooksLikePersonName(cleaned) ? cleaned : null;
    }

    /// <summary>
    /// Um nome precisa de pelo menos duas palavras e de uma palavra com três letras ou mais. Isso
    /// descarta rótulo solto e ruído de OCR sem descartar nomes curtos legítimos.
    /// </summary>
    public static bool LooksLikePersonName(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var words = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return words.Length >= 2 && words.Any(word => word.Length >= 3);
    }
}
