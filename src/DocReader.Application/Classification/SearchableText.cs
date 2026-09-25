using System.Text;
using DocReader.Application.Extraction;

namespace DocReader.Application.Classification;

/// <summary>
/// OCR text prepared for evidence matching. Real OCR glues words together
/// (<c>REPUBLICAFEDERATIVADOBRASIL</c>), splits them, misreads an accent or one letter, so two views
/// of the text are kept (words, and letters with no spacing) and a pattern is looked up in the one that
/// fits its length.
/// </summary>
public sealed class SearchableText
{
    /// <summary>Patterns with fewer letters than this are matched as whole words only.</summary>
    public const int MinimumCompactLength = 8;

    private const int LettersPerTolerance = 8;
    private const int MaximumEdits = 3;

    private readonly string _words;
    private readonly string _compact;

    private SearchableText(string words)
    {
        _words = $" {words} ";
        _compact = words.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    public int Length => _compact.Length;

    /// <summary>Upper case letters and digits only, words separated by a single space.</summary>
    public static SearchableText From(string? text) => new(ToWords(text));

    /// <summary>
    /// Looks for the first pattern present. Short patterns (<c>CEP</c>, <c>CPF</c>) must be whole words, so
    /// they do not fire inside <c>RECEPCAO</c>. Longer ones are found without regard to spacing and
    /// tolerate one OCR error per eight letters, up to three.
    /// </summary>
    public TextMatch? FindAny(IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = Find(pattern);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    public TextMatch? Find(string pattern)
    {
        var words = ToWords(pattern);
        var compact = words.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (compact.Length == 0)
        {
            return null;
        }

        if (compact.Length < MinimumCompactLength)
        {
            return _words.Contains($" {words} ", StringComparison.Ordinal) ? new TextMatch(pattern, 0) : null;
        }

        if (_compact.Contains(compact, StringComparison.Ordinal))
        {
            return new TextMatch(pattern, 0);
        }

        var allowed = Math.Min(MaximumEdits, compact.Length / LettersPerTolerance);
        var edits = BestEditDistance(compact, _compact, allowed);

        return edits <= allowed ? new TextMatch(pattern, edits) : null;
    }

    private static string ToWords(string? value)
    {
        var normalized = TextNormalization.ForMatching(value);
        var builder = new StringBuilder(normalized.Length);
        var lastWasSpace = true;

        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Smallest edit distance between the pattern and any substring of the text (Sellers), stopping as
    /// soon as it is within <paramref name="allowed"/>. Returns allowed + 1 when nothing is close enough.
    /// </summary>
    private static int BestEditDistance(string pattern, string text, int allowed)
    {
        var column = new int[pattern.Length + 1];
        for (var row = 0; row <= pattern.Length; row++)
        {
            column[row] = row;
        }

        var best = int.MaxValue;
        foreach (var character in text)
        {
            var diagonal = column[0];
            column[0] = 0;

            for (var row = 1; row <= pattern.Length; row++)
            {
                var above = column[row];
                var cost = pattern[row - 1] == character ? 0 : 1;
                column[row] = Math.Min(Math.Min(above + 1, column[row - 1] + 1), diagonal + cost);
                diagonal = above;
            }

            best = Math.Min(best, column[pattern.Length]);
            if (best == 0)
            {
                break;
            }
        }

        return best <= allowed ? best : allowed + 1;
    }
}
