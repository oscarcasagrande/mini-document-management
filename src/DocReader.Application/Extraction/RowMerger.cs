namespace DocReader.Application.Extraction;

/// <summary>
/// Junta os blocos de OCR que caem na mesma linha visual. O detector parte uma linha longa de caracteres
/// repetidos, como a MRZ ("IDUT…&lt;&lt;&lt;&lt;&lt;&lt;" e "&lt;&lt;&lt;&lt;&lt;" em blocos separados), e quem procura uma linha inteira
/// precisa dela de volta. Sem coordenadas as linhas passam como vieram.
/// </summary>
internal static class RowMerger
{
    /// <summary>Metade da altura da linha separa "mesma linha visual" de "linha vizinha".</summary>
    private const decimal SameRowTolerance = 0.5m;

    public static IReadOnlyList<OcrTextLine> Merge(IReadOnlyList<OcrTextLine> lines)
    {
        var rows = new List<List<OcrTextLine>>();

        foreach (var line in lines)
        {
            var row = rows.Count > 0 ? rows[^1] : null;

            if (row is not null && line.Box is not null && SameRow(row, line))
            {
                row.Add(line);
            }
            else
            {
                rows.Add([line]);
            }
        }

        return [.. rows.Select(Combine)];
    }

    private static bool SameRow(List<OcrTextLine> row, OcrTextLine line)
    {
        var anchor = row[0];
        if (anchor.Box is not { } anchorBox || line.Box is not { } box || anchor.PageNumber != line.PageNumber)
        {
            return false;
        }

        var height = Math.Max(Math.Min(anchorBox.Height, box.Height), 1m);

        return Math.Abs(anchorBox.CenterY - box.CenterY) <= SameRowTolerance * height;
    }

    private static OcrTextLine Combine(List<OcrTextLine> row)
    {
        if (row.Count == 1)
        {
            return row[0];
        }

        var ordered = row.OrderBy(line => line.Box!.Value.Left).ToArray();
        var left = ordered.Min(line => line.Box!.Value.Left);
        var top = ordered.Min(line => line.Box!.Value.Top);
        var right = ordered.Max(line => line.Box!.Value.Right);
        var bottom = ordered.Max(line => line.Box!.Value.Bottom);
        var confidences = ordered.Where(line => line.Confidence is not null).Select(line => line.Confidence!.Value).ToArray();

        return new OcrTextLine(
            row[0].Index,
            row[0].PageNumber,
            string.Concat(ordered.Select(line => line.Text)),
            confidences.Length == 0 ? null : confidences.Min(),
            [left, top, right, top, right, bottom, left, bottom]);
    }
}
