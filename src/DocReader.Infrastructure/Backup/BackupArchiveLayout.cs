using System.Globalization;
using System.Text.RegularExpressions;

namespace DocReader.Infrastructure.Backup;

/// <summary>
/// What a backup archive contains, relative to its root:
/// <list type="bullet">
/// <item><c>data.sql</c>: plain SQL dump (<c>pg_dump --format=plain --clean --if-exists</c>).</item>
/// <item><c>storage_manifest.json</c>: one entry per document of the same snapshot.</item>
/// <item><c>files/{documentId}</c>: the bytes of the documents stored in file system or database repositories.</item>
/// <item><c>checksums.sha256</c>: <c>&lt;sha256&gt;  &lt;path&gt;</c> for every file above, as <c>sha256sum</c> writes it.</item>
/// <item><c>checksums.sha256.hmac</c>: HMAC-SHA256 of <c>checksums.sha256</c> under a key derived from the installation's encryption key.</item>
/// </list>
/// </summary>
public static partial class BackupArchiveLayout
{
    public const string DatabaseDump = "data.sql";
    public const string StorageManifest = "storage_manifest.json";
    public const string Checksums = "checksums.sha256";
    public const string ChecksumsSignature = "checksums.sha256.hmac";
    public const string FilesDirectory = "files";

    /// <summary>
    /// Tables of operational state of this installation that a backup does not carry and a restore must not touch: the
    /// job rows (a restore would otherwise drop the row of the job running it) and the read-only gate.
    /// </summary>
    public static readonly IReadOnlyList<string> ExcludedTables = ["public.backup_jobs", "public.restore_jobs", "public.system_state"];

    public static string FilePathFor(Guid documentId) =>
        string.Create(CultureInfo.InvariantCulture, $"{FilesDirectory}/{documentId:D}");

    /// <summary>Forward slashes, no dot segments, no absolute path: the only names an archive entry may have.</summary>
    public static bool IsSafeRelativePath(string path) =>
        !string.IsNullOrEmpty(path) &&
        path.Length <= 256 &&
        SafePathPattern().IsMatch(path) &&
        !path.Split('/').Any(segment => segment is "" or "." or "..");

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePathPattern();
}
