using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocReader.Application.Extraction;

/// <summary>Trecho achado no texto corrido, com a linha de OCR onde ele começa, como evidência.</summary>
internal sealed record ProseMatch(Match Match, string Original, OcrTextLine Line);

/// <summary>
/// O texto de um contrato quebra a frase em linhas de OCR, então rótulo e valor nem sempre ficam na
/// mesma linha. Este índice junta as linhas em um texto só, mantém a correspondência posição a posição
/// com a linha de origem e procura em uma versão sem acento e em caixa alta, de mesmo tamanho, para que
/// a posição achada valha também no texto original.
/// </summary>
internal sealed class ProseIndex
{
    private readonly List<(int Start, OcrTextLine Line)> _starts = [];

    public ProseIndex(IReadOnlyList<OcrTextLine> lines)
    {
        var original = new StringBuilder();

        foreach (var line in lines.Where(line => !line.IsEmpty))
        {
            if (original.Length > 0)
            {
                original.Append(' ');
            }

            _starts.Add((original.Length, line));
            original.Append(line.Text);
        }

        Original = original.ToString();
        Folded = Fold(Original);
    }

    /// <summary>Texto como veio do OCR, linhas separadas por espaço.</summary>
    public string Original { get; }

    /// <summary>Mesmo tamanho de <see cref="Original"/>, sem acento e em caixa alta.</summary>
    public string Folded { get; }

    public IEnumerable<ProseMatch> Matches(Regex pattern, bool onOriginal = false)
    {
        var source = onOriginal ? Original : Folded;

        foreach (Match match in pattern.Matches(source))
        {
            yield return new ProseMatch(match, Original.Substring(match.Index, match.Length), LineAt(match.Index));
        }
    }

    public OcrTextLine LineAt(int offset)
    {
        var line = _starts[0].Line;

        foreach (var (start, candidate) in _starts)
        {
            if (start > offset)
            {
                break;
            }

            line = candidate;
        }

        return line;
    }

    /// <summary>Um caractere de saída para cada caractere de entrada: sem acento e em caixa alta.</summary>
    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            var decomposed = character.ToString().Normalize(NormalizationForm.FormD);
            var baseCharacter = decomposed.Length > 0 &&
                                CharUnicodeInfo.GetUnicodeCategory(decomposed[0]) != UnicodeCategory.NonSpacingMark
                ? decomposed[0]
                : character;

            builder.Append(char.ToUpperInvariant(baseCharacter));
        }

        return builder.ToString();
    }
}
