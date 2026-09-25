namespace DocReader.Application.Extraction;

/// <summary>Distância de edição limitada, para tolerar a letra que o OCR troca num rótulo.</summary>
internal static class EditDistance
{
    /// <summary>Verdadeiro quando as duas cadeias diferem em no máximo <paramref name="maximum"/> edições.</summary>
    public static bool Within(string left, string right, int maximum)
    {
        if (Math.Abs(left.Length - right.Length) > maximum)
        {
            return false;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            var rowBest = current[0];

            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1), previous[column - 1] + cost);
                rowBest = Math.Min(rowBest, current[column]);
            }

            if (rowBest > maximum)
            {
                return false;
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length] <= maximum;
    }
}
