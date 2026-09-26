namespace DocReader.Infrastructure.Persistence;

/// <summary>Helpers for <c>ILIKE</c> filters.</summary>
internal static class Like
{
    /// <summary>The escape character to pass to <c>EF.Functions.ILike</c>.</summary>
    public const string Escape = "\\";

    /// <summary>
    /// Builds a contains pattern with the wildcard characters of LIKE escaped, so a value such as
    /// <c>100%</c> is matched literally instead of turning into a wildcard.
    /// </summary>
    public static string Contains(string value)
    {
        var escaped = value.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

        return $"%{escaped}%";
    }
}
