namespace DocReader.Domain.Backup;

/// <summary>
/// One request to back up the database and the locally stored document files into a <c>.tar.gz</c> archive. The
/// worker claims a pending job, builds the archive and saves it in <see cref="StorageRepositoryId"/>; this row tracks
/// that run and, once completed, where the archive is.
/// </summary>
public sealed class BackupJob
{
    public const int MaxErrorMessageLength = 2048;

    private BackupJob()
    {
    }

    public Guid Id { get; private init; }

    public BackupJobStatus Status { get; private set; }

    public DateTimeOffset RequestedAt { get; private init; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>The storage repository the archive is (or will be) saved in, chosen when the job is requested.</summary>
    public Guid StorageRepositoryId { get; private init; }

    /// <summary>Where the archive was saved in <see cref="StorageRepositoryId"/>; null until completed.</summary>
    public string? StorageKey { get; private set; }

    /// <summary>Download name of the archive, <c>backup-{id}.tar.gz</c>; null until completed.</summary>
    public string? FileName { get; private set; }

    public long? SizeBytes { get; private set; }

    /// <summary>SHA-256 (hex) of the archive file itself, not of its contents.</summary>
    public string? ChecksumSha256 { get; private set; }

    /// <summary>Documents listed in the archive's storage manifest.</summary>
    public int? DocumentCount { get; private set; }

    /// <summary>Document files copied into the archive (only those in file system or database repositories).</summary>
    public int? FilesArchived { get; private set; }

    public string? ErrorMessage { get; private set; }

    public static BackupJob Create(Guid id, Guid storageRepositoryId, DateTimeOffset now) => new()
    {
        Id = id,
        StorageRepositoryId = storageRepositoryId,
        Status = BackupJobStatus.Pending,
        RequestedAt = now
    };

    public static string FileNameFor(Guid id) => $"backup-{id:D}.tar.gz";

    public void MarkRunning(DateTimeOffset now)
    {
        Status = BackupJobStatus.Running;
        StartedAt = now;
    }

    public void MarkCompleted(
        DateTimeOffset now,
        string storageKey,
        long sizeBytes,
        string checksumSha256,
        int documentCount,
        int filesArchived)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(checksumSha256);

        Status = BackupJobStatus.Completed;
        CompletedAt = now;
        StorageKey = storageKey;
        FileName = FileNameFor(Id);
        SizeBytes = sizeBytes;
        ChecksumSha256 = checksumSha256;
        DocumentCount = documentCount;
        FilesArchived = filesArchived;
        ErrorMessage = null;
    }

    public void MarkFailed(DateTimeOffset now, string errorMessage)
    {
        Status = BackupJobStatus.Failed;
        CompletedAt = now;
        ErrorMessage = Truncate(errorMessage);
    }

    private static string Truncate(string message) =>
        message.Length <= MaxErrorMessageLength ? message : message[..MaxErrorMessageLength];
}
