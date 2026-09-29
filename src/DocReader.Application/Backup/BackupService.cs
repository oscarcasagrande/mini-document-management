using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using DocReader.Domain.Backup;
using DocReader.Domain.Storage;
using Microsoft.Extensions.Logging;

namespace DocReader.Application.Backup;

/// <summary>
/// Backs up the database and the locally stored document files. <see cref="EnqueueAsync"/> only records the request;
/// the work happens in <see cref="ProcessAsync"/>, which the worker drives, so the endpoint returns immediately. The
/// archive is saved through <see cref="IFileStorage"/>, in the default repository or the one the request names: the
/// same adapters documents use, so a cloud repository makes an off-site backup without a second upload mechanism.
/// </summary>
public sealed class BackupService(
    IBackupJobRepository jobs,
    IStorageRepositoryStore repositories,
    IBackupArchiveBuilder archives,
    IFileStorage storage,
    TimeProvider timeProvider,
    ILogger<BackupService> logger)
{
    public const string ArchiveExtension = ".tgz";
    public const string ArchiveMimeType = "application/gzip";

    public async Task<BackupJob> EnqueueAsync(Guid? storageRepositoryId, CancellationToken ct)
    {
        var repository = storageRepositoryId is { } id
            ? await repositories.FindByIdAsync(id, ct).ConfigureAwait(false)
                ?? throw new UnprocessableRequestException(
                    "STORAGE_REPOSITORY_NOT_FOUND",
                    $"There is no storage repository with the id {id}.")
            : await repositories.FindDefaultAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("There is no default storage repository.");

        EnsureUsableTarget(repository);

        var now = timeProvider.GetUtcNow();
        var job = BackupJob.Create(Guid.CreateVersion7(now), repository.Id, now);

        await jobs.AddAsync(job, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Backup requested. backupJobId={BackupJobId} storageRepositoryId={StorageRepositoryId}",
            job.Id,
            repository.Id);

        return job;
    }

    public async Task<BackupJob> GetAsync(Guid id, CancellationToken ct) =>
        await jobs.FindByIdAsync(id, ct).ConfigureAwait(false)
        ?? throw new ResourceNotFoundException("backup-job", id.ToString());

    /// <summary>Opens the archive of a completed backup, for download.</summary>
    public async Task<(BackupJob Job, Stream Content)> OpenArchiveAsync(Guid id, CancellationToken ct)
    {
        var job = await GetAsync(id, ct).ConfigureAwait(false);

        if (job.Status != BackupJobStatus.Completed || job.StorageKey is null)
        {
            throw new ResourceConflictException(
                "BACKUP_NOT_COMPLETED",
                "The backup has no archive yet: it is pending, running or failed. Poll the job until its status is COMPLETED.");
        }

        try
        {
            var content = await storage.OpenReadAsync(job.StorageRepositoryId, job.StorageKey, ct).ConfigureAwait(false);

            return (job, content);
        }
        catch (FileNotFoundException)
        {
            throw new ResourceNotFoundException("backup-archive", id.ToString());
        }
    }

    /// <summary>
    /// Runs one backup job to completion. Callable directly, without a running worker loop, which is what makes it
    /// testable. A failure marks the job failed with the reason and does not throw; a cancellation marks it failed and
    /// rethrows, so a stopping worker does not leave a job running forever.
    /// </summary>
    public async Task ProcessAsync(Guid jobId, CancellationToken ct)
    {
        var job = await jobs.FindByIdAsync(jobId, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("backup-job", jobId.ToString());

        if (job.Status == BackupJobStatus.Pending)
        {
            job.MarkRunning(timeProvider.GetUtcNow());
            await jobs.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        logger.LogInformation(
            "BACKUP_STARTED backupJobId={BackupJobId} storageRepositoryId={StorageRepositoryId}",
            job.Id,
            job.StorageRepositoryId);

        var started = timeProvider.GetTimestamp();

        try
        {
            await using var archive = await archives.BuildAsync(OpenContentAsync, ct).ConfigureAwait(false);

            StoredFile stored;
            await using (var content = archive.OpenRead())
            {
                stored = await storage
                    .SaveAsync(job.StorageRepositoryId, content, new FileMetadata(job.Id, ArchiveExtension, ArchiveMimeType, timeProvider.GetUtcNow()), ct)
                    .ConfigureAwait(false);
            }

            // Reading database-stored files may have gone through the same unit of work: reload before the final change.
            var completed = await jobs.FindByIdAsync(jobId, ct).ConfigureAwait(false) ?? job;
            completed.MarkCompleted(
                timeProvider.GetUtcNow(),
                stored.StorageKey,
                archive.SizeBytes,
                archive.ChecksumSha256,
                archive.DocumentCount,
                archive.FilesArchived);
            await jobs.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogInformation(
                "BACKUP_COMPLETED backupJobId={BackupJobId} storageRepositoryId={StorageRepositoryId} storageKey={StorageKey} sizeBytes={SizeBytes} documentCount={DocumentCount} filesArchived={FilesArchived} durationMs={DurationMs}",
                completed.Id,
                completed.StorageRepositoryId,
                stored.StorageKey,
                archive.SizeBytes,
                archive.DocumentCount,
                archive.FilesArchived,
                (long)timeProvider.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await MarkFailedAsync(jobId, job, "Interrupted: the worker stopped while the backup was running. Request a new backup.")
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await MarkFailedAsync(jobId, job, exception.Message).ConfigureAwait(false);

            logger.LogError(
                "BACKUP_FAILED backupJobId={BackupJobId} errorType={ErrorType}",
                jobId,
                exception.GetType().Name);
        }
    }

    private async Task MarkFailedAsync(Guid jobId, BackupJob loaded, string reason)
    {
        // The failure is recorded even when the run was cancelled: that is exactly when it must not stay RUNNING.
        var failed = await jobs.FindByIdAsync(jobId, CancellationToken.None).ConfigureAwait(false) ?? loaded;
        failed.MarkFailed(timeProvider.GetUtcNow(), reason);
        await jobs.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The file of a document in a repository of this installation (file system or database) goes into the archive; a
    /// cloud-stored one keeps only its manifest entry, because the provider already keeps it and downloading every blob
    /// would make the backup as large and slow as the whole bucket.
    /// </summary>
    private async Task<Stream?> OpenContentAsync(BackupManifestEntry entry, CancellationToken ct)
    {
        if (!entry.IsLocallyStored)
        {
            return null;
        }

        try
        {
            return await storage.OpenReadAsync(entry.StorageRepositoryId, entry.StorageKey, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            logger.LogWarning(
                "Backup skipped a missing document file. documentId={DocumentId} storageKey={StorageKey}",
                entry.DocumentId,
                entry.StorageKey);

            return null;
        }
    }

    private static void EnsureUsableTarget(StorageRepository repository)
    {
        if (!repository.Active)
        {
            throw new UnprocessableRequestException(
                "STORAGE_REPOSITORY_INACTIVE",
                $"The storage repository {repository.Code} is inactive and takes no new files.");
        }

        if (repository.Provider == StorageProvider.Database)
        {
            throw new UnprocessableRequestException(
                "BACKUP_TARGET_UNSUPPORTED",
                "A backup cannot be saved in a DATABASE repository: it would live inside the database it protects, be dropped by a restore and be copied into every later backup. Name a FILE_SYSTEM, AZURE_BLOB_STORAGE or AWS_S3 repository in storageRepositoryId.");
        }
    }
}
