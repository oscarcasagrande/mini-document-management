namespace DocReader.Domain.Backup;

/// <summary>
/// One request to restore a backup archive. The archive was uploaded, its checksums verified and saved in
/// <see cref="ArchiveStorageRepositoryId"/> before this row was created; the worker claims a pending job, replays the
/// database dump in a single transaction, writes the archived document files back and verifies the result.
/// </summary>
public sealed class RestoreJob
{
    public const int MaxErrorMessageLength = 2048;

    private RestoreJob()
    {
    }

    public Guid Id { get; private init; }

    public RestoreJobStatus Status { get; private set; }

    public DateTimeOffset RequestedAt { get; private init; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Where the uploaded archive waits for the worker. It is deleted once the job finishes.</summary>
    public Guid ArchiveStorageRepositoryId { get; private init; }

    public string ArchiveStorageKey { get; private init; } = string.Empty;

    public long ArchiveSizeBytes { get; private init; }

    /// <summary>SHA-256 (hex) of the uploaded archive file.</summary>
    public string ArchiveChecksumSha256 { get; private init; } = string.Empty;

    /// <summary>Documents listed in the archive's storage manifest.</summary>
    public int DocumentCount { get; private init; }

    /// <summary>Document files written back to their storage repository; null until the job finishes.</summary>
    public int? FilesRestored { get; private set; }

    public string? ErrorMessage { get; private set; }

    public static RestoreJob Create(
        Guid id,
        Guid archiveStorageRepositoryId,
        string archiveStorageKey,
        long archiveSizeBytes,
        string archiveChecksumSha256,
        int documentCount,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveStorageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveChecksumSha256);

        return new RestoreJob
        {
            Id = id,
            Status = RestoreJobStatus.Pending,
            RequestedAt = now,
            ArchiveStorageRepositoryId = archiveStorageRepositoryId,
            ArchiveStorageKey = archiveStorageKey,
            ArchiveSizeBytes = archiveSizeBytes,
            ArchiveChecksumSha256 = archiveChecksumSha256,
            DocumentCount = documentCount
        };
    }

    public void MarkRunning(DateTimeOffset now)
    {
        Status = RestoreJobStatus.Running;
        StartedAt = now;
    }

    public void MarkCompleted(DateTimeOffset now, int filesRestored)
    {
        Status = RestoreJobStatus.Completed;
        CompletedAt = now;
        FilesRestored = filesRestored;
        ErrorMessage = null;
    }

    public void MarkFailed(DateTimeOffset now, string errorMessage, int? filesRestored = null)
    {
        Status = RestoreJobStatus.Failed;
        CompletedAt = now;
        FilesRestored = filesRestored ?? FilesRestored;
        ErrorMessage = errorMessage.Length <= MaxErrorMessageLength ? errorMessage : errorMessage[..MaxErrorMessageLength];
    }
}
