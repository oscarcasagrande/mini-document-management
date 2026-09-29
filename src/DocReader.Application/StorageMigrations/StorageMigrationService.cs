using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using DocReader.Domain;
using DocReader.Domain.Documents;
using DocReader.Domain.Files;
using DocReader.Domain.StorageMigrations;
using Microsoft.Extensions.Logging;

namespace DocReader.Application.StorageMigrations;

/// <summary>
/// Moves documents from one storage repository to another: copies each original to the target and repoints the
/// document there, leaving the original in the source untouched. <see cref="EnqueueAsync"/> only records the job;
/// the work happens in <see cref="ProcessAsync"/>, which the worker drives, so the endpoint that starts a migration
/// returns immediately. <see cref="CancelAsync"/> stops a job before its next batch without undoing what it moved.
/// </summary>
public sealed class StorageMigrationService(
    IStorageMigrationJobRepository jobs,
    IStorageRepositoryStore repositories,
    IFileStorage storage,
    TimeProvider timeProvider,
    ILogger<StorageMigrationService> logger)
{
    /// <summary>Documents selected per batch. The job's status is checked again before each one.</summary>
    public const int BatchSize = 10;

    public async Task<StorageMigrationJob> EnqueueAsync(
        Guid sourceRepositoryId,
        Guid targetRepositoryId,
        StorageMigrationFilter? filter,
        CancellationToken ct)
    {
        if (sourceRepositoryId == targetRepositoryId)
        {
            throw new RequestValidationException(
                "STORAGE_MIGRATION_SAME_REPOSITORY",
                "The source and the target repository are the same. Choose a different target.");
        }

        filter = Normalize(filter);

        if (filter is { UploadedFrom: { } from, UploadedTo: { } to } && from > to)
        {
            throw new RequestValidationException(
                "STORAGE_MIGRATION_INVALID_DATE_RANGE",
                "documentFilter.uploadedFrom is later than documentFilter.uploadedTo.");
        }

        var source = await repositories.FindByIdAsync(sourceRepositoryId, ct).ConfigureAwait(false)
            ?? throw new UnprocessableRequestException(
                "STORAGE_REPOSITORY_NOT_FOUND",
                $"There is no storage repository with the id {sourceRepositoryId} (sourceRepositoryId).");

        var target = await repositories.FindByIdAsync(targetRepositoryId, ct).ConfigureAwait(false)
            ?? throw new UnprocessableRequestException(
                "STORAGE_REPOSITORY_NOT_FOUND",
                $"There is no storage repository with the id {targetRepositoryId} (targetRepositoryId).");

        if (!target.Active)
        {
            throw new UnprocessableRequestException(
                "STORAGE_REPOSITORY_INACTIVE",
                $"The storage repository {target.Code} is inactive and takes no new documents. Activate it before migrating to it.");
        }

        var now = timeProvider.GetUtcNow();
        var job = StorageMigrationJob.Create(Guid.CreateVersion7(now), source.Id, target.Id, filter, now);

        await jobs.AddAsync(job, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Storage migration requested. storageMigrationJobId={StorageMigrationJobId} sourceRepositoryId={SourceRepositoryId} targetRepositoryId={TargetRepositoryId} filtered={Filtered}",
            job.Id,
            job.SourceRepositoryId,
            job.TargetRepositoryId,
            !job.Filter.IsEmpty);

        return job;
    }

    public async Task<StorageMigrationJob> GetAsync(Guid jobId, CancellationToken ct) =>
        await jobs.FindByIdAsync(jobId, ct).ConfigureAwait(false)
        ?? throw new ResourceNotFoundException("storage-migration-job", jobId.ToString());

    /// <summary>
    /// Cancels a pending or running job. A running job stops before its next batch; nothing already moved is moved
    /// back.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">There is no such job.</exception>
    /// <exception cref="ResourceConflictException">The job already ended.</exception>
    public async Task<StorageMigrationJob> CancelAsync(Guid jobId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        var job = await jobs.UpdateLockedAsync(
                jobId,
                candidate =>
                {
                    if (!candidate.CanCancel)
                    {
                        throw new ResourceConflictException(
                            "STORAGE_MIGRATION_NOT_CANCELLABLE",
                            $"The storage migration job is {EnumNaming.ToUpperSnakeCase(candidate.Status)} and can no longer be cancelled. Only PENDING or RUNNING jobs can.");
                    }

                    candidate.Cancel(now);
                },
                ct)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("storage-migration-job", jobId.ToString());

        logger.LogInformation(
            "Storage migration cancelled. storageMigrationJobId={StorageMigrationJobId} documentsMigrated={DocumentsMigrated}",
            job.Id,
            job.DocumentsMigrated);

        return job;
    }

    /// <summary>
    /// Runs one job to its end: batches through the matching documents of its source until none is left, or until the
    /// job is cancelled, then marks it completed. A document that fails is logged, counted and skipped for the rest of
    /// the run; it never stops the others. Callable directly, without a running worker loop, which is what makes it
    /// testable.
    /// </summary>
    public async Task ProcessAsync(Guid jobId, CancellationToken ct)
    {
        var job = await jobs.FindByIdAsync(jobId, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("storage-migration-job", jobId.ToString());

        if (job.Status == StorageMigrationStatus.Pending)
        {
            job = await jobs.UpdateLockedAsync(jobId, pending => pending.MarkRunning(timeProvider.GetUtcNow()), ct).ConfigureAwait(false)
                ?? job;
        }

        if (job.Status != StorageMigrationStatus.Running)
        {
            return;
        }

        try
        {
            var excludedIds = new HashSet<Guid>();
            var migrated = 0;
            var failed = 0;
            var cancelled = false;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (await jobs.GetStatusAsync(jobId, ct).ConfigureAwait(false) != StorageMigrationStatus.Running)
                {
                    cancelled = true;
                    break;
                }

                var batch = await jobs.FindBatchAsync(job, BatchSize, excludedIds, ct).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    break;
                }

                var batchMigrated = 0;
                var batchFailed = 0;

                foreach (var documentId in batch)
                {
                    try
                    {
                        var moved = await jobs.MigrateDocumentAsync(
                                documentId,
                                job,
                                (document, token) => CopyAsync(document, job.TargetRepositoryId, token),
                                timeProvider.GetUtcNow(),
                                ct)
                            .ConfigureAwait(false);

                        if (moved)
                        {
                            batchMigrated++;
                        }
                        else
                        {
                            // No longer eligible (moved or purged meanwhile): never select it again in this run.
                            excludedIds.Add(documentId);
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        excludedIds.Add(documentId);
                        batchFailed++;

                        // Identifiers and the error type only: never the file name or anything read from the document.
                        logger.LogError(
                            exception,
                            "Storage migration failed for a document. storageMigrationJobId={StorageMigrationJobId} documentId={DocumentId} targetRepositoryId={TargetRepositoryId} errorType={ErrorType}",
                            job.Id,
                            documentId,
                            job.TargetRepositoryId,
                            exception.GetType().Name);
                    }
                }

                migrated += batchMigrated;
                failed += batchFailed;

                await jobs.UpdateLockedAsync(jobId, running => running.RecordBatch(batchMigrated, batchFailed), ct).ConfigureAwait(false);
            }

            var finished = await jobs.UpdateLockedAsync(jobId, running => running.MarkCompleted(timeProvider.GetUtcNow()), ct).ConfigureAwait(false);

            logger.LogInformation(
                "Storage migration finished. storageMigrationJobId={StorageMigrationJobId} status={Status} documentsMigrated={DocumentsMigrated} documentsFailed={DocumentsFailed} cancelled={Cancelled}",
                jobId,
                finished is null ? null : EnumNaming.ToUpperSnakeCase(finished.Status),
                migrated,
                failed,
                cancelled);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await jobs.UpdateLockedAsync(jobId, running => running.MarkFailed(timeProvider.GetUtcNow(), exception.Message), ct).ConfigureAwait(false);

            logger.LogError(
                exception,
                "Storage migration failed. storageMigrationJobId={StorageMigrationJobId} errorType={ErrorType}",
                jobId,
                exception.GetType().Name);
        }
    }

    /// <summary>
    /// Copies the original of <paramref name="document"/> into <paramref name="targetRepositoryId"/> and returns the key
    /// it got there. The source is only read.
    /// </summary>
    private async Task<string> CopyAsync(Document document, Guid targetRepositoryId, CancellationToken ct)
    {
        Stream content;
        try
        {
            content = await storage.OpenReadAsync(document.StorageRepositoryId, document.StorageKey, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            throw new DocumentContentMissingException(document.Id);
        }

        await using (content.ConfigureAwait(false))
        {
            // The key is rebuilt from the detected MIME type, as at upload: the client's file name never reaches it.
            var metadata = new FileMetadata(
                document.Id,
                FileSignatureInspector.ExtensionForMimeType(document.MimeType),
                document.MimeType,
                timeProvider.GetUtcNow());

            var stored = await storage.SaveAsync(targetRepositoryId, content, metadata, ct).ConfigureAwait(false);

            if (stored.SizeBytes != document.SizeBytes)
            {
                throw new InvalidDataException(
                    $"The copy has {stored.SizeBytes} bytes and the original {document.SizeBytes}; the document stays on its source.");
            }

            return stored.StorageKey;
        }
    }

    private static StorageMigrationFilter? Normalize(StorageMigrationFilter? filter)
    {
        if (filter is null)
        {
            return null;
        }

        var documentType = string.IsNullOrWhiteSpace(filter.DocumentType)
            ? null
            : filter.DocumentType.Trim().ToUpperInvariant();

        return filter with
        {
            DocumentType = documentType,
            UploadedFrom = filter.UploadedFrom?.ToUniversalTime(),
            UploadedTo = filter.UploadedTo?.ToUniversalTime()
        };
    }
}
