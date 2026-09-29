using System.Globalization;
using System.Text.RegularExpressions;
using DocReader.Application.Abstractions;

namespace DocReader.Infrastructure.Storage;

/// <summary>
/// The storage key layout every adapter uses: <c>documents/yyyy/MM/dd/{documentId}/original{ext}</c>. The date
/// prefix only keeps directories/prefixes small; identity comes from the UUID, never from a client supplied name.
/// </summary>
internal static partial class StorageKeyLayout
{
    private const string OriginalFileName = "original";

    public static string BuildKey(FileMetadata metadata)
    {
        var extension = NormalizeExtension(metadata.Extension);
        var day = metadata.CreatedAt.UtcDateTime;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"documents/{day:yyyy}/{day:MM}/{day:dd}/{metadata.DocumentId:D}/{OriginalFileName}{extension}");
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var candidate = extension.StartsWith('.') ? extension : "." + extension;

        return SafeExtensionPattern().IsMatch(candidate)
            ? candidate.ToLowerInvariant()
            : throw new ArgumentException($"Extension {extension} is not a safe storage extension.", nameof(extension));
    }

    /// <summary>Only lowercase alphanumeric extensions of up to four characters are accepted.</summary>
    [GeneratedRegex(@"^\.[a-z0-9]{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeExtensionPattern();
}
