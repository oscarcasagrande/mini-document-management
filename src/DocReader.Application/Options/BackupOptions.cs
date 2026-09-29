using System.ComponentModel.DataAnnotations;

namespace DocReader.Application.Options;

/// <summary>Where backups and restores stage their files and how they reach the PostgreSQL client tools.</summary>
public sealed class BackupOptions
{
    public const string SectionName = "DocReader:Backup";

    /// <summary>
    /// Directory for the temporary staging areas of a backup (dump, copied files, archive) and of a restore
    /// (extracted archive). Empty means a <c>docreader-backup</c> folder under the system temp directory. Each run uses
    /// its own subfolder and removes it when it ends.
    /// </summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary><c>pg_dump</c> executable: a name found on PATH, or an absolute path. Its major version must match the server's.</summary>
    [Required]
    public string PgDumpPath { get; set; } = "pg_dump";

    /// <summary><c>psql</c> executable: a name found on PATH, or an absolute path.</summary>
    [Required]
    public string PsqlPath { get; set; } = "psql";

    /// <summary>Longest a single <c>pg_dump</c> or <c>psql</c> run may take before it is killed.</summary>
    [Range(typeof(TimeSpan), "00:00:10", "1.00:00:00")]
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    /// Ceiling on the bytes a restore will extract from one archive (the sum of its entries), so a small, highly
    /// compressed upload cannot fill the disk.
    /// </summary>
    [Range(1024L * 1024, long.MaxValue)]
    public long MaxExtractedBytes { get; set; } = 20L * 1024 * 1024 * 1024;
}
