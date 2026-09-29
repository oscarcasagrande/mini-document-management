namespace DocReader.Domain.StorageMigrations;

/// <summary>
/// One request to copy the documents of a storage repository into another and repoint them there. The worker claims
/// a pending job and works through the matching documents in batches; this row tracks that run. The originals are
/// never deleted from the source.
/// </summary>
public sealed class StorageMigrationJob
{
    private StorageMigrationJob()
    {
    }

    public Guid Id { get; private init; }

    public Guid SourceRepositoryId { get; private init; }

    public Guid TargetRepositoryId { get; private init; }

    public string? FilterDocumentType { get; private init; }

    public Guid? FilterProductServiceId { get; private init; }

    public DateTimeOffset? FilterUploadedFrom { get; private init; }

    public DateTimeOffset? FilterUploadedTo { get; private init; }

    public StorageMigrationStatus Status { get; private set; }

    public DateTimeOffset RequestedAt { get; private init; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Documents moved to the target so far.</summary>
    public int DocumentsMigrated { get; private set; }

    /// <summary>Per-document failures so far. A failed document stays on the source and is counted each time it fails.</summary>
    public int DocumentsFailed { get; private set; }

    public string? ErrorMessage { get; private set; }

    public StorageMigrationFilter Filter => new(FilterDocumentType, FilterProductServiceId, FilterUploadedFrom, FilterUploadedTo);

    /// <summary>A job can only be cancelled before it reaches an end.</summary>
    public bool CanCancel => Status is StorageMigrationStatus.Pending or StorageMigrationStatus.Running;

    public static StorageMigrationJob Create(
        Guid id,
        Guid sourceRepositoryId,
        Guid targetRepositoryId,
        StorageMigrationFilter? filter,
        DateTimeOffset now)
    {
        if (sourceRepositoryId == targetRepositoryId)
        {
            throw new ArgumentException("The source and the target repository must differ.", nameof(targetRepositoryId));
        }

        filter ??= StorageMigrationFilter.None;

        return new StorageMigrationJob
        {
            Id = id,
            SourceRepositoryId = sourceRepositoryId,
            TargetRepositoryId = targetRepositoryId,
            FilterDocumentType = filter.DocumentType,
            FilterProductServiceId = filter.ProductServiceId,
            FilterUploadedFrom = filter.UploadedFrom?.ToUniversalTime(),
            FilterUploadedTo = filter.UploadedTo?.ToUniversalTime(),
            Status = StorageMigrationStatus.Pending,
            RequestedAt = now
        };
    }

    public void MarkRunning(DateTimeOffset now)
    {
        if (Status != StorageMigrationStatus.Pending)
        {
            return;
        }

        Status = StorageMigrationStatus.Running;
        StartedAt = now;
    }

    /// <summary>Adds what one batch did to the running totals. Counted even after a cancel, so the totals stay true.</summary>
    public void RecordBatch(int migrated, int failed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(migrated);
        ArgumentOutOfRangeException.ThrowIfNegative(failed);

        DocumentsMigrated += migrated;
        DocumentsFailed += failed;
    }

    /// <summary>Ends a running job. A job cancelled while it ran stays cancelled.</summary>
    public void MarkCompleted(DateTimeOffset now)
    {
        if (Status != StorageMigrationStatus.Running)
        {
            return;
        }

        Status = StorageMigrationStatus.Completed;
        CompletedAt = now;
    }

    /// <summary>Ends a job that broke as a whole (not a single document). A job cancelled while it ran stays cancelled.</summary>
    public void MarkFailed(DateTimeOffset now, string errorMessage)
    {
        if (Status is not (StorageMigrationStatus.Pending or StorageMigrationStatus.Running))
        {
            return;
        }

        Status = StorageMigrationStatus.Failed;
        CompletedAt = now;
        ErrorMessage = errorMessage;
    }

    /// <summary>Stops the job before its next batch. Documents already moved are not moved back.</summary>
    /// <exception cref="InvalidOperationException">The job already ended.</exception>
    public void Cancel(DateTimeOffset now)
    {
        if (!CanCancel)
        {
            throw new InvalidOperationException($"Cannot cancel a storage migration job in status {Status}.");
        }

        Status = StorageMigrationStatus.Cancelled;
        CompletedAt = now;
    }
}
