namespace DocReader.Api.Http;

/// <summary>
/// The end to end correlation id. It is accepted from the caller when it looks sane and generated
/// otherwise, and it travels on every response (PRD section 16).
/// </summary>
public static class CorrelationId
{
    public const string HeaderName = "X-Correlation-Id";

    private const int MaxLength = 128;

    public const string ItemKey = "DocReader.CorrelationId";

    /// <summary>
    /// Sanitizes a caller supplied value. A value with control characters, or one that is too long,
    /// is discarded rather than echoed back into logs and headers.
    /// </summary>
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            return null;
        }

        foreach (var character in trimmed)
        {
            var isAllowed = char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':';
            if (!isAllowed)
            {
                return null;
            }
        }

        return trimmed;
    }

    public static string Generate() => Guid.NewGuid().ToString("N");
}
