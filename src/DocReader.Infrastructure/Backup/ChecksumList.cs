using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DocReader.Infrastructure.Backup;

/// <summary>
/// The <c>checksums.sha256</c> format, the one <c>sha256sum</c> writes and <c>sha256sum -c</c> checks: one
/// <c>&lt;hex&gt;  &lt;relative path&gt;</c> per line, sorted by path, LF endings, UTF-8 without BOM.
/// </summary>
public static partial class ChecksumList
{
    public static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.Asynchronous | FileOptions.SequentialScan });

        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    public static byte[] Format(IReadOnlyDictionary<string, string> hashByPath)
    {
        var text = new StringBuilder();
        foreach (var path in hashByPath.Keys.Order(StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"{hashByPath[path]}  {path}\n");
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ToString());
    }

    /// <summary>Parses the list, or returns null when any line is malformed, a path is unsafe or listed twice.</summary>
    public static IReadOnlyDictionary<string, string>? Parse(byte[] content)
    {
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var match = LinePattern().Match(line);
            if (!match.Success)
            {
                return null;
            }

            var path = match.Groups["path"].Value;
            if (!BackupArchiveLayout.IsSafeRelativePath(path) || !result.TryAdd(path, match.Groups["hash"].Value))
            {
                return null;
            }
        }

        return result;
    }

    [GeneratedRegex("^(?<hash>[0-9a-f]{64})  (?<path>[^\r\n]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LinePattern();
}
