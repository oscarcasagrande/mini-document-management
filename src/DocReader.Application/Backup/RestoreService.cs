using System.Security.Cryptography;
using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using DocReader.Domain.Backup;
using Microsoft.Extensions.Logging;

namespace DocReader.Application.Backup;

/// <summary>
/// Restores a backup archive. <see cref="EnqueueAsync"/> verifies the upload synchronously (a corrupt or foreign archive
/// is refused before anything is queued), saves it through <see cref="IFileStorage"/> so the worker can reach it, and
/// records the job. <see cref="ProcessAsync"/>, driven by the worker, raises the read-only gate, replays the dump in one
/// transaction, verifies the documents and writes the archived files back.
/// </summary>
public sealed class RestoreService(
    IRestoreJobRepository jobs,
    IStorageRepositoryStore repositories,
    IBackupArchiveReader reader,
    IDatabaseRestorer database,
    ISystemStateStore systemState,
    IFileStorage storage,
    TimeProvider timeProvider,
    ILogger<RestoreService> logger)
{
    /// <exception cref="BackupArchiveInvalidException">The archive fails verification.</exception>
    /// <exception cref="ResourceConflictException">Another restore is pending or running.</exception>
    public async Task<RestoreJob> EnqueueAsync(Stream archive, CancellationToken ct)
    {
        if (!archive.CanSeek)
        {
            throw new ArgumentException("The archive stream must be seekable: it is read to verify it and again to store it.", nameof(archive));
        }

        if (await jobs.HasActiveAsync(ct).ConfigureAwait(false))
        {
            throw RestoreInProgress();
        }

        int documentCount;
        await using (var extracted = await reader.ExtractAndVerifyAsync(archive, ct).ConfigureAwait(false))
        {
            documentCount = extracted.Documents.Count;
        }

        archive.Position = 0;
        var checksum = Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, ct).ConfigureAwait(false));
        archive.Position = 0;

        var repository = await repositories.FindDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("There is no default storage repository.");

        var now = timeProvider.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        var stored = await storage
            .SaveAsync(repository.Id, archive, new FileMetadata(id, BackupService.ArchiveExtension, BackupService.ArchiveMimeType, now), ct)
            .ConfigureAwait(false);

        var job = RestoreJob.Create(id, repository.Id, stored.StorageKey, stored.SizeBytes, checksum, documentCount, now);

        try
        {
            await jobs.AddAsync(job, ct).ConfigureAwait(false);
        }
        catch
        {
            await TryDeleteArchiveAsync(repository.Id, stored.StorageKey).ConfigureAwait(false);
            throw;
        }

        logger.LogInformation(
            "Restore requested. restoreJobId={RestoreJobId} archiveSizeBytes={ArchiveSizeBytes} archiveSha256={ArchiveSha256} documentCount={DocumentCount}",
            job.Id,
            stored.SizeBytes,
            checksum,
            documentCount);

        return job;
    }

    public async Task<RestoreJob> GetAsync(Guid id, CancellationToken ct) =>
        await jobs.FindByIdAsync(id, ct).ConfigureAwait(false)
        ?? throw new ResourceNotFoundException("restore-job", id.ToString());

    /// <summary>
    /// Runs one restore job to completion. The read-only gate is raised before the database is touched and lowered in a
    /// <c>finally</c>, whatever happens. A failure of the dump itself leaves the database as it was (the replay is one
    /// transaction); a failure after it (a document of the manifest missing from the dump, a file that could not be
    /// written back) is reported on the job, with the database already restored.
    /// </summary>
    public async Task ProcessAsync(Guid jobId, CancellationToken ct)
    {
        var job = await jobs.FindByIdAsync(jobId, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("restore-job", jobId.ToString());

        if (job.Status == RestoreJobStatus.Pending)
        {
            job.MarkRunning(timeProvider.GetUtcNow());
            await jobs.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var archiveRepositoryId = job.ArchiveStorageRepositoryId;
        var archiveKey = job.ArchiveStorageKey;
        var started = timeProvider.GetTimestamp();
        var readOnlyRaised = false;
        var filesRestored = 0;

        logger.LogInformation("RESTORE_STARTED restoreJobId={RestoreJobId} documentCount={DocumentCount}", job.Id, job.DocumentCount);

        try
        {
            await systemState.SetReadOnlyAsync(true, timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
            readOnlyRaised = true;

            await using var archive = await storage.OpenReadAsync(archiveRepositoryId, archiveKey, ct).ConfigureAwait(false);
            await using var extracted = await reader.ExtractAndVerifyAsync(archive, ct).ConfigureAwait(false);

            await database.RestoreAsync(extracted.DatabaseDumpPath, ct).ConfigureAwait(false);

            // Everything loaded before this point describes the database that was just replaced.
            jobs.ForgetLoadedState();

            logger.LogInformation("Restore replayed the database dump. restoreJobId={RestoreJobId}", jobId);

            var documentIds = extracted.Documents.Select(entry => entry.DocumentId).ToList();
            var missing = await database.CountMissingDocumentsAsync(documentIds, ct).ConfigureAwait(false);
            if (missing > 0)
            {
                throw new InvalidOperationException(
                    $"The database dump was restored and committed, but {missing} of the {documentIds.Count} documents listed in the storage manifest are not in it: the dump and the manifest disagree. No files were written back.");
            }

            filesRestored = await RestoreFilesAsync(extracted, ct).ConfigureAwait(false);

            var completed = await jobs.FindByIdAsync(jobId, ct).ConfigureAwait(false) ?? job;
            completed.MarkCompleted(timeProvider.GetUtcNow(), filesRestored);
            await jobs.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogInformation(
                "RESTORE_COMPLETED restoreJobId={RestoreJobId} documentCount={DocumentCount} filesRestored={FilesRestored} durationMs={DurationMs}",
                jobId,
                documentIds.Count,
                filesRestored,
                (long)timeProvider.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await MarkFailedAsync(jobId, job, InterruptedMessage, filesRestored).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            if (exception is FilesNotRestoredException partial)
            {
                filesRestored = partial.Restored;
            }

            await MarkFailedAsync(jobId, job, exception.Message, filesRestored).ConfigureAwait(false);

            logger.LogError(
                "RESTORE_FAILED restoreJobId={RestoreJobId} errorType={ErrorType} filesRestored={FilesRestored}",
                jobId,
                exception.GetType().Name,
                filesRestored);
        }
        finally
        {
            if (readOnlyRaised)
            {
                await LowerReadOnlyGateAsync(jobId).ConfigureAwait(false);
            }

            await TryDeleteArchiveAsync(archiveRepositoryId, archiveKey).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After a worker restart: a job left running was interrupted (psql dies with the worker, and PostgreSQL rolls back
    /// the transaction of a dropped connection), so it is marked failed and the read-only gate is lowered. Assumes a
    /// single worker process runs restores, which is how the compose file deploys it.
    /// </summary>
    public async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        var running = await jobs.ListRunningAsync(ct).ConfigureAwait(false);

        foreach (var jobId in running)
        {
            var job = await jobs.FindByIdAsync(jobId, ct).ConfigureAwait(false);
            if (job is null)
            {
                continue;
            }

            job.MarkFailed(timeProvider.GetUtcNow(), InterruptedMessage);
            await jobs.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogWarning("Restore job found running at startup and marked failed. restoreJobId={RestoreJobId}", jobId);
        }

        if (await systemState.IsReadOnlyAsync(ct).ConfigureAwait(false))
        {
            await systemState.SetReadOnlyAsync(false, timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
            logger.LogWarning("Read-only gate found raised at startup with no restore running; lowered.");
        }
    }

    private const string InterruptedMessage =
        "Interrupted: the worker stopped while the restore was running. If psql had not committed, PostgreSQL rolled the whole restore back; if it had, the database is restored but the document files and the verification did not run. Check the data and restore again if needed.";

    /// <summary>
    /// Writes each archived file back to the repository and key its document records. The adapters derive the key from
    /// the document id, the upload instant and the extension, so the key they return is compared with the recorded one;
    /// a mismatch is removed and counted as a failure rather than leaving a document pointing at nothing.
    /// </summary>
    private async Task<int> RestoreFilesAsync(IExtractedBackup extracted, CancellationToken ct)
    {
        var restored = 0;
        var failed = new List<Guid>();

        foreach (var entry in extracted.Documents)
        {
            if (entry.ArchivePath is null)
            {
                continue;
            }

            try
            {
                await using var content = await extracted.OpenArchivedFileAsync(entry.ArchivePath, ct).ConfigureAwait(false);
                var metadata = new FileMetadata(entry.DocumentId, ExtensionOf(entry.StorageKey), entry.MimeType, entry.UploadedAt);
                var stored = await storage.SaveAsync(entry.StorageRepositoryId, content, metadata, ct).ConfigureAwait(false);

                if (!string.Equals(stored.StorageKey, entry.StorageKey, StringComparison.Ordinal))
                {
                    await storage.DeleteAsync(entry.StorageRepositoryId, stored.StorageKey, ct).ConfigureAwait(false);
                    failed.Add(entry.DocumentId);
                    continue;
                }

                restored++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    "Restore could not write a document file back. documentId={DocumentId} storageRepositoryId={StorageRepositoryId} errorType={ErrorType}",
                    entry.DocumentId,
                    entry.StorageRepositoryId,
                    exception.GetType().Name);

                failed.Add(entry.DocumentId);
            }
        }

        if (failed.Count > 0)
        {
            throw new FilesNotRestoredException(restored, failed);
        }

        return restored;
    }

    private static string ExtensionOf(string storageKey)
    {
        var name = storageKey[(storageKey.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');

        return dot < 0 ? string.Empty : name[dot..];
    }

    private async Task MarkFailedAsync(Guid jobId, RestoreJob loaded, string reason, int filesRestored)
    {
        try
        {
            var failed = await jobs.FindByIdAsync(jobId, CancellationToken.None).ConfigureAwait(false) ?? loaded;
            failed.MarkFailed(timeProvider.GetUtcNow(), reason, filesRestored);
            await jobs.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError("Could not record the failure of a restore job. restoreJobId={RestoreJobId} errorType={ErrorType}", jobId, exception.GetType().Name);
        }
    }

    private async Task LowerReadOnlyGateAsync(Guid jobId)
    {
        try
        {
            await systemState.SetReadOnlyAsync(false, timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The worker lowers it again at its next start (RecoverInterruptedAsync).
            logger.LogError("Could not lower the read-only gate after a restore. restoreJobId={RestoreJobId} errorType={ErrorType}", jobId, exception.GetType().Name);
        }
    }

    private async Task TryDeleteArchiveAsync(Guid repositoryId, string storageKey)
    {
        try
        {
            await storage.DeleteAsync(repositoryId, storageKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Could not remove an uploaded restore archive. storageKey={StorageKey} errorType={ErrorType}", storageKey, exception.GetType().Name);
        }
    }

    private static ResourceConflictException RestoreInProgress() => new(
        "RESTORE_ALREADY_IN_PROGRESS",
        "Another restore is pending or running. Wait for it to finish before starting a new one.");
}
