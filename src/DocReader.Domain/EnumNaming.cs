using System.Text;

namespace DocReader.Domain;

/// <summary>
/// Single place that maps enum members to the upper snake case wire and storage names, so the API,
/// the database and the logs all spell a status the same way: <c>OcrRunning</c> is always
/// <c>OCR_RUNNING</c>.
/// </summary>
public static class EnumNaming
{
    public static string ToUpperSnakeCase<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        ToUpperSnakeCase(value.ToString());

    public static string ToUpperSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);

        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];
            if (index > 0 && char.IsUpper(current) && !char.IsUpper(name[index - 1]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(current));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses a value in upper snake case, plain case insensitive form, or anything in between.
    /// </summary>
    public static bool TryParse<TEnum>(string? value, out TEnum parsed)
        where TEnum : struct, Enum
    {
        parsed = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var compact = value.Trim().Replace("_", string.Empty, StringComparison.Ordinal);

        // Enum.TryParse also accepts the numeric value of a member. Clients talk in names, so a
        // number is treated as an invalid value instead of silently matching a member.
        if (compact.All(char.IsAsciiDigit))
        {
            return false;
        }

        return Enum.TryParse(compact, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }

    /// <summary>Every member of the enum in its wire form, for documentation and validation messages.</summary>
    public static IReadOnlyList<string> NamesOf<TEnum>()
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Select(ToUpperSnakeCase).ToArray();
}
