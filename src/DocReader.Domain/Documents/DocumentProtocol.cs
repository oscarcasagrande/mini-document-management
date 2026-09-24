using System.Globalization;

namespace DocReader.Domain.Documents;

/// <summary>
/// Human readable protocol in the form <c>DOC-yyyyMMdd-NNNNNN</c>, unique per day.
/// </summary>
public static class DocumentProtocol
{
    public const string Prefix = "DOC";

    private const int SequenceDigits = 6;

    public static string Format(DateOnly day, long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}-{day:yyyyMMdd}-{sequence.ToString(CultureInfo.InvariantCulture).PadLeft(SequenceDigits, '0')}");
    }

    /// <summary>
    /// Validates the shape of a protocol received from a client, so a malformed value never
    /// reaches the database as a query parameter.
    /// </summary>
    public static bool IsWellFormed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('-');
        if (parts.Length != 3 || parts[0] != Prefix || parts[1].Length != 8 || parts[2].Length < SequenceDigits)
        {
            return false;
        }

        if (!DateOnly.TryParseExact(parts[1], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return false;
        }

        return parts[2].All(char.IsAsciiDigit);
    }
}
