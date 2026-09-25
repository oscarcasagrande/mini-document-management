using System.Text.RegularExpressions;

namespace DocReader.Application.Extraction;

/// <summary>Valor candidato de um campo, com a linha que o sustenta e a penalidade de distância ao rótulo.</summary>
internal sealed record LabelledValue(OcrTextLine Line, string Text, decimal Penalty);

/// <summary>
/// Procura o valor de um campo a partir do rótulo. As três formas em que os documentos brasileiros
/// escrevem um campo têm caminho próprio:
///
/// 1. na mesma linha, depois de dois-pontos ("Nome: MARIA");
/// 2. em outra linha, à direita ou abaixo do rótulo, achada pela geometria;
/// 3. na linha seguinte da ordem de leitura, quando o provedor não mandou coordenadas.
///
/// Linhas que são rótulos conhecidos do tipo nunca são devolvidas como valor: em documento de duas
/// colunas, a linha ao lado de "NOME" costuma ser o rótulo "DATA DE NASCIMENTO".
/// </summary>
internal sealed partial class LineSearch
{
    private const decimal StepPenalty = 0.05m;

    /// <summary>Rótulos mais curtos que isso só casam por igualdade: "NOME" não pode casar "NOME SOCIAL".</summary>
    private const int LongLabelLength = 12;

    private const int FuzzyLabelLength = 8;
    private const int MaximumLabelEdits = 3;

    /// <summary>
    /// Bloco bem mais alto que largo e mais alto que duas linhas do rótulo é texto girado (a numeração
    /// vertical na borda de uma carteira) e nunca é o valor de um campo. Parágrafo de várias linhas é mais
    /// largo que alto, então não cai aqui.
    /// </summary>
    private const decimal RotatedHeightOverWidth = 1.5m;

    private const decimal RotatedHeightOverLabel = 2m;

    private readonly IReadOnlyList<OcrTextLine> _lines;
    private readonly string[] _knownLabels;
    private readonly ExtractionTrace? _trace;

    public LineSearch(IReadOnlyList<OcrTextLine> lines, IEnumerable<string> knownLabels, ExtractionTrace? trace = null)
    {
        _lines = lines;
        _knownLabels = [.. knownLabels.Distinct(StringComparer.Ordinal)];
        _trace = trace;
    }

    /// <summary>
    /// Roda a regra de um campo e, quando há rastro, registra nela os rótulos e candidatos que a busca viu.
    /// Sem rastro é só a chamada.
    /// </summary>
    public T Field<T>(string field, Func<T> read) => _trace is null ? read() : _trace.Run([field], read);

    /// <summary>Como <see cref="Field{T}(string, Func{T})"/>, para uma regra que produz vários campos de uma vez.</summary>
    public T Fields<T>(string[] fields, Func<T> read) => _trace is null ? read() : _trace.Run(fields, read);

    public IReadOnlyList<OcrTextLine> Lines => _lines;

    /// <summary>Candidatos a valor dos rótulos, do mais provável ao menos, sem repetir.</summary>
    public IEnumerable<LabelledValue> After(IReadOnlyList<string> labels, int lookahead = 2)
    {
        var seen = new HashSet<(int, string)>();

        foreach (var label in labels)
        {
            var labelLines = new List<(OcrTextLine Line, LabelMatch Match)>();
            foreach (var line in _lines)
            {
                var match = MatchLabel(line, label);
                if (match is null || IsLongerLabel(line, label))
                {
                    continue;
                }

                labelLines.Add((line, match));
            }

            _trace?.LabelSearched(label, [.. labelLines.Select(entry => entry.Line.Index)]);

            foreach (var (line, match) in labelLines)
            {
                if (!string.IsNullOrWhiteSpace(match.InlineValue) && seen.Add((line.Index, match.InlineValue)))
                {
                    _trace?.CandidateSeen(line, match.InlineValue, 0m);
                    yield return new LabelledValue(line, match.InlineValue, 0m);
                }

                foreach (var candidate in Around(line, lookahead))
                {
                    if (seen.Add((candidate.Line.Index, candidate.Text)))
                    {
                        _trace?.CandidateSeen(candidate.Line, candidate.Text, candidate.Penalty);
                        yield return candidate;
                    }
                }
            }
        }
    }

    /// <summary>As linhas de rótulo que casam algum dos rótulos, para regras que precisam da posição.</summary>
    public IEnumerable<OcrTextLine> LabelLines(IReadOnlyList<string> labels)
    {
        foreach (var label in labels)
        {
            _trace?.LabelSearched(label, [.. _lines.Where(line => MatchLabel(line, label) is not null).Select(line => line.Index)]);
        }

        return _lines.Where(line => labels.Any(label => MatchLabel(line, label) is not null));
    }

    /// <summary>
    /// Linhas logo abaixo de <paramref name="start"/>, alinhadas a ela, para valores de várias linhas
    /// (endereço, filiação). Sem coordenadas, são as seguintes na ordem de leitura.
    /// </summary>
    public IReadOnlyList<OcrTextLine> Following(OcrTextLine start, int count)
    {
        var result = new List<OcrTextLine>();
        var current = start;

        while (result.Count < count)
        {
            var next = current.Box is not null
                ? Below(current, 1).FirstOrDefault()
                : ReadingOrder(current, 1).FirstOrDefault();

            if (next is null || IsKnownLabel(next) || LabelBetween(current, next))
            {
                break;
            }

            result.Add(next);
            current = next;
        }

        return result;
    }

    /// <summary>
    /// Há um rótulo conhecido entre as duas linhas, na mesma coluna: o valor de baixo é do campo desse rótulo, não
    /// continuação. O <c>Below</c> descarta rótulos ao procurar valor, então sem esta checagem a busca os atravessa.
    /// </summary>
    private bool LabelBetween(OcrTextLine upper, OcrTextLine lower)
    {
        if (upper.Box is not { } top || lower.Box is not { } bottom)
        {
            return false;
        }

        var height = Math.Max(top.Height, 1m);

        return _lines.Any(line => line.PageNumber == upper.PageNumber
            && line.Index != upper.Index
            && line.Index != lower.Index
            && line.Box is { } box
            && box.CenterY > top.CenterY
            && box.CenterY < bottom.CenterY
            && HorizontallyAligned(top, box, height)
            && IsKnownLabel(line));
    }

    public bool IsKnownLabel(OcrTextLine line) => _knownLabels.Any(label => MatchLabel(line, label) is not null);

    private IEnumerable<LabelledValue> Around(OcrTextLine label, int lookahead)
    {
        var spatial = 0;

        if (label.Box is not null)
        {
            foreach (var right in RightOf(label).Take(1))
            {
                spatial++;
                yield return new LabelledValue(right, right.Text, 0m);
            }

            var rank = 0;
            foreach (var below in Below(label, lookahead))
            {
                spatial++;
                yield return new LabelledValue(below, below.Text, StepPenalty * rank);
                rank++;
            }
        }

        // Só quando a geometria não achou nada: com coordenadas, a linha seguinte na ordem de leitura
        // costuma ser de outra coluna, e devolvê-la seria inventar um vizinho.
        if (spatial == 0)
        {
            var offset = 1;
            foreach (var next in ReadingOrder(label, lookahead))
            {
                yield return new LabelledValue(next, next.Text, StepPenalty * offset);
                offset++;
            }
        }
    }

    private IEnumerable<OcrTextLine> RightOf(OcrTextLine label)
    {
        var box = label.Box!.Value;
        var height = Math.Max(box.Height, 1m);

        return _lines
            .Where(candidate => candidate.Index != label.Index
                && candidate.PageNumber == label.PageNumber
                && !candidate.IsEmpty
                && !IsKnownLabel(candidate)
                && candidate.Box is { } other
                && !IsRotated(other, height)
                && Math.Abs(other.CenterY - box.CenterY) <= 0.6m * height
                && other.Left >= box.Right - (0.3m * height))
            .OrderBy(candidate => candidate.Box!.Value.Left)
            .Select(MergeRow);
    }

    private IEnumerable<OcrTextLine> Below(OcrTextLine label, int max)
    {
        var box = label.Box!.Value;
        var height = Math.Max(box.Height, 1m);

        var consumed = new HashSet<int>();
        var result = new List<OcrTextLine>();

        var ordered = _lines
            .Where(candidate => candidate.Index != label.Index
                && candidate.PageNumber == label.PageNumber
                && !candidate.IsEmpty
                && !IsKnownLabel(candidate)
                && candidate.Box is { } other
                && !IsRotated(other, height)
                && other.Top >= box.Bottom - (0.35m * height)
                && other.Top - box.Bottom <= 3.5m * height
                && HorizontallyAligned(box, other, height))
            .OrderBy(candidate => candidate.Box!.Value.Top)
            .ThenBy(candidate => candidate.Box!.Value.Left);

        foreach (var candidate in ordered)
        {
            // O que já foi juntado a uma linha anterior não volta como outro candidato.
            if (consumed.Contains(candidate.Index))
            {
                continue;
            }

            var merged = MergeRow(candidate, consumed);
            result.Add(merged);

            if (result.Count == max)
            {
                break;
            }
        }

        return result;
    }

    private OcrTextLine MergeRow(OcrTextLine seed) => MergeRow(seed, []);

    /// <summary>
    /// O OCR às vezes parte um valor em blocos na mesma linha visual ("SAO" e "PAULO/SP", ou um nome em
    /// dois pedaços). Junta, da esquerda para a direita, os blocos que começam logo depois do fim do
    /// anterior; uma folga de uma altura e meia de linha separa palavra de coluna.
    /// </summary>
    private OcrTextLine MergeRow(OcrTextLine seed, HashSet<int> consumed)
    {
        if (seed.Box is not { } start)
        {
            return seed;
        }

        var merged = seed;
        var box = start;
        var height = Math.Max(start.Height, 1m);

        while (true)
        {
            var next = _lines
                .Where(candidate => candidate.Index != seed.Index
                    && !consumed.Contains(candidate.Index)
                    && candidate.PageNumber == seed.PageNumber
                    && !candidate.IsEmpty
                    && !IsKnownLabel(candidate)
                    && candidate.Box is { } other
                    && Math.Abs(other.CenterY - box.CenterY) <= 0.5m * height
                    && other.Left >= box.Right - (0.3m * height)
                    && other.Left - box.Right <= 1.5m * height)
                .OrderBy(candidate => candidate.Box!.Value.Left)
                .FirstOrDefault();

            if (next?.Box is not { } nextBox)
            {
                return merged;
            }

            consumed.Add(next.Index);
            box = new LineBox(box.Left, Math.Min(box.Top, nextBox.Top), nextBox.Right, Math.Max(box.Bottom, nextBox.Bottom));

            merged = new OcrTextLine(
                seed.Index,
                seed.PageNumber,
                $"{merged.Text} {next.Text}",
                merged.Confidence is null || next.Confidence is null ? merged.Confidence ?? next.Confidence : Math.Min(merged.Confidence.Value, next.Confidence.Value),
                [box.Left, box.Top, box.Right, box.Top, box.Right, box.Bottom, box.Left, box.Bottom]);
        }
    }

    /// <summary>
    /// A linha é, por inteiro, um rótulo mais longo que começa com este: "TITULO DO ESTABELECIMENTO (NOME DE
    /// FANTASIA)" não pode servir ao rótulo curto "TITULO DO ESTABELECIMENTO" e devolver o resto como valor.
    /// </summary>
    private bool IsLongerLabel(OcrTextLine line, string label) =>
        _knownLabels.Any(other => other.Length > label.Length
            && other.StartsWith(label, StringComparison.Ordinal)
            && Keys(line.Normalized).Any(key => key.Trim() == other || key.Split(':')[0].Trim() == other));

    private static bool IsRotated(LineBox box, decimal labelHeight) =>
        box.Height > RotatedHeightOverWidth * box.Width && box.Height > RotatedHeightOverLabel * labelHeight;

    private static bool HorizontallyAligned(LineBox label, LineBox other, decimal height)
    {
        var overlap = Math.Min(label.Right, other.Right) - Math.Max(label.Left, other.Left);

        return overlap > 0m || Math.Abs(other.Left - label.Left) <= 2m * height;
    }

    private IEnumerable<OcrTextLine> ReadingOrder(OcrTextLine label, int count)
    {
        for (var offset = 1; offset <= count && label.Index + offset < _lines.Count; offset++)
        {
            var candidate = _lines[label.Index + offset];
            if (candidate.PageNumber != label.PageNumber || candidate.IsEmpty || IsKnownLabel(candidate))
            {
                continue;
            }

            yield return candidate;
        }
    }

    private static LabelMatch? MatchLabel(OcrTextLine line, string label)
    {
        foreach (var key in Keys(line.Normalized))
        {
            var colon = key.IndexOf(':', StringComparison.Ordinal);
            var labelPart = colon >= 0 ? key[..colon].Trim() : key.Trim();

            if (labelPart == label)
            {
                return new LabelMatch(colon >= 0 ? InlineAfterColon(line.Text) : null);
            }

            if (label.Length >= LongLabelLength && labelPart.StartsWith(label, StringComparison.Ordinal))
            {
                return colon >= 0
                    ? new LabelMatch(InlineAfterColon(line.Text))
                    : new LabelMatch(InlineAfterWords(line.Text, label));
            }
        }

        return MatchTolerant(line, label) ? new LabelMatch(null) : null;
    }

    /// <summary>
    /// O que o casamento exato perde em documento real: palavras coladas, sinal de "Nº" ou "1ª" trocado, numerador
    /// duplo ("2e 1 NOME E SOBRENOME"), rótulo bilíngue ("NOME/NAME") e uma letra errada pelo OCR ("FILUACAO").
    /// Rótulo curto só casa por igualdade; a partir de oito letras tolera uma troca a cada oito, até três.
    /// </summary>
    private static bool MatchTolerant(OcrTextLine line, string label)
    {
        var wanted = LabelKeys.Compact(label);
        if (wanted.Length == 0)
        {
            return false;
        }

        var allowed = wanted.Length >= FuzzyLabelLength ? Math.Min(MaximumLabelEdits, wanted.Length / FuzzyLabelLength) : 0;

        foreach (var key in line.LabelKeys)
        {
            if (key == wanted || (allowed > 0 && EditDistance.Within(key, wanted, allowed)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A linha como veio e sem o numerador de campo de CNH ("4b VALIDADE").</summary>
    private static IEnumerable<string> Keys(string normalized)
    {
        yield return normalized;

        var stripped = EnumeratorPrefix().Replace(normalized, string.Empty);
        if (!ReferenceEquals(stripped, normalized) && stripped.Length > 0 && stripped != normalized)
        {
            yield return stripped;
        }
    }

    private static string? InlineAfterColon(string original)
    {
        var index = original.IndexOf(':', StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var remainder = original[(index + 1)..].Trim();

        return remainder.Length == 0 ? null : remainder;
    }

    /// <summary>Descarta as palavras do rótulo e devolve o resto da linha, quando o OCR juntou rótulo e valor.</summary>
    private static string? InlineAfterWords(string original, string label)
    {
        var wordCount = label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var tokens = original.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length <= wordCount)
        {
            return null;
        }

        var remainder = string.Join(' ', tokens.Skip(wordCount)).TrimStart(':', '-', ' ');

        return remainder.Length == 0 ? null : remainder;
    }

    private sealed record LabelMatch(string? InlineValue);

    /// <summary>Numerador de campo da CNH e de formulários: "4b ", "9 ", "12. ".</summary>
    [GeneratedRegex(@"^\d{1,2}[A-Z]?\s*[-.)]?\s+(?=[A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex EnumeratorPrefix();
}
