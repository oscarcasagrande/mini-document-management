using DocReader.Domain.Catalog;
using DocReader.Domain.Retention;
using DocReader.Domain.Storage;

namespace DocReader.Domain.Documents;

/// <summary>
/// A document accepted for processing. The binary itself lives in <see cref="StorageKey"/>,
/// never in the database.
/// </summary>
public sealed class Document
{
    private readonly List<DocumentEvent> _events = [];

    private Document()
    {
    }

    public Guid Id { get; private init; }

    /// <summary>Human readable protocol, format <c>DOC-yyyyMMdd-NNNNNN</c>.</summary>
    public string Protocol { get; private init; } = string.Empty;

    /// <summary>Name supplied by the client. Metadata only: it never reaches the storage key.</summary>
    public string OriginalFileName { get; private init; } = string.Empty;

    public string StorageKey { get; private init; } = string.Empty;

    /// <summary>The repository <see cref="StorageKey"/> lives in. Fixed at upload: changing the default later never moves a file.</summary>
    public Guid StorageRepositoryId { get; private init; }

    /// <summary>MIME type detected from the file signature, not the declared one.</summary>
    public string MimeType { get; private init; } = string.Empty;

    public long SizeBytes { get; private init; }

    public string Sha256 { get; private init; } = string.Empty;

    public int PageCount { get; private init; }

    public UploadChannel UploadChannel { get; private init; }

    public string? ExternalReference { get; private init; }

    public string? ExpectedDocumentType { get; private init; }

    /// <summary>Product or service the document was uploaded for, when the caller said so.</summary>
    public Guid? ProductServiceId { get; private init; }

    public ProductService? ProductService { get; private set; }

    public string? DetectedDocumentType { get; private set; }

    public decimal? ClassificationConfidence { get; private set; }

    public DocumentStatus Status { get; private set; }

    public DateTimeOffset UploadedAt { get; private init; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>When the document becomes eligible for purge; null when no retention was ever applied.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>The policy that set <see cref="ExpiresAt"/>. Null when that policy was deleted since; <see cref="RetentionDays"/> still says what it was.</summary>
    public Guid? RetentionPolicyId { get; private set; }

    public RetentionPolicy? RetentionPolicy { get; private set; }

    /// <summary>Days of retention in force when the date was set, so the deadline can be explained after the policy changes.</summary>
    public int? RetentionDays { get; private set; }

    public DateTimeOffset? PurgedAt { get; private set; }

    public string? LastErrorCode { get; private set; }

    public string? LastErrorMessage { get; private set; }

    public IReadOnlyCollection<DocumentEvent> Events => _events;

    /// <summary>
    /// Creates a document that is already stored and about to be queued.
    /// </summary>
    public static Document Accept(
        Guid id,
        string protocol,
        string originalFileName,
        string storageKey,
        string mimeType,
        long sizeBytes,
        string sha256,
        int pageCount,
        UploadChannel uploadChannel,
        string? externalReference,
        string? expectedDocumentType,
        DateTimeOffset uploadedAt,
        Guid? productServiceId = null,
        RetentionPolicy? retentionPolicy = null,
        Guid? storageRepositoryId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);

        var document = new Document
        {
            Id = id,
            Protocol = protocol,
            OriginalFileName = originalFileName,
            StorageKey = storageKey,
            StorageRepositoryId = storageRepositoryId ?? StorageRepository.DefaultRepositoryId,
            MimeType = mimeType,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            PageCount = pageCount,
            UploadChannel = uploadChannel,
            ExternalReference = externalReference,
            ExpectedDocumentType = expectedDocumentType,
            ProductServiceId = productServiceId,
            UploadedAt = uploadedAt,
            Status = DocumentStatus.Received
        };

        if (retentionPolicy is not null)
        {
            document.ApplyRetention(retentionPolicy, uploadedAt);
        }

        document._events.Add(DocumentEvent.Create(id, DocumentEventTypes.Received, DocumentStatus.Received, uploadedAt));
        document.Status = DocumentStatus.Stored;
        document._events.Add(DocumentEvent.Create(id, DocumentEventTypes.Stored, DocumentStatus.Stored, uploadedAt));

        return document;
    }

    /// <summary>
    /// Sets the purge date to <paramref name="basis"/> plus the days of <paramref name="policy"/>. The basis is when
    /// the current cycle started: the upload, or the reprocess request.
    /// </summary>
    public void ApplyRetention(RetentionPolicy policy, DateTimeOffset basis)
    {
        ArgumentNullException.ThrowIfNull(policy);

        RetentionPolicyId = policy.Id;
        RetentionDays = policy.RetentionDays;
        ExpiresAt = basis.AddDays(policy.RetentionDays);
    }

    /// <summary>
    /// Applies another policy to the same cycle: the basis is recovered from the current date and days, so a
    /// document that was already counting keeps its start and only the duration (and the policy) change.
    /// </summary>
    public void ReapplyRetention(RetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var basis = ExpiresAt is { } expires && RetentionDays is { } days ? expires.AddDays(-days) : UploadedAt;

        ApplyRetention(policy, basis);
    }

    /// <summary>Whether the purge job may take the document: past its date, in a final status, not purged yet.</summary>
    public bool IsPurgeable(DateTimeOffset now) =>
        ExpiresAt is { } expires
        && expires <= now
        && Status is DocumentStatus.Completed or DocumentStatus.Failed or DocumentStatus.Rejected;

    /// <summary>Records that the original file was removed at the end of the retention period. Idempotent.</summary>
    public void MarkPurged(DateTimeOffset occurredAt)
    {
        if (Status == DocumentStatus.Purged)
        {
            return;
        }

        Status = DocumentStatus.Purged;
        PurgedAt = occurredAt;
        _events.Add(DocumentEvent.Create(Id, DocumentEventTypes.Purged, DocumentStatus.Purged, occurredAt, "RETENTION_EXPIRED"));
    }

    /// <summary>
    /// Marks the document as queued for the worker. Called in the same transaction that persists the job.
    /// </summary>
    public void MarkQueued(DateTimeOffset occurredAt, string? details = null)
    {
        if (Status is not (DocumentStatus.Stored or DocumentStatus.Failed or DocumentStatus.Completed))
        {
            throw new InvalidOperationException($"Cannot queue a document in status {Status}.");
        }

        Status = DocumentStatus.Queued;
        CompletedAt = null;
        LastErrorCode = null;
        LastErrorMessage = null;
        _events.Add(DocumentEvent.Create(Id, DocumentEventTypes.Queued, DocumentStatus.Queued, occurredAt, details));
    }


    /// <summary>
    /// Moves an in-flight document to the next stage of the pipeline and records it on the timeline.
    /// </summary>
    public void AdvanceTo(DocumentStatus stage, string eventType, DateTimeOffset occurredAt, string? details = null)
    {
        if (Status is not (DocumentStatus.Queued or DocumentStatus.Preprocessing or DocumentStatus.OcrRunning
            or DocumentStatus.Classifying or DocumentStatus.Extracting))
        {
            throw new InvalidOperationException($"Cannot advance a document in status {Status} to {stage}.");
        }

        Status = stage;
        _events.Add(DocumentEvent.Create(Id, eventType, stage, occurredAt, details));
    }

    /// <summary>
    /// Records progress inside the current stage, such as a page that was read. The status does not change.
    /// </summary>
    public void RecordProgress(string eventType, DateTimeOffset occurredAt, string? details = null) =>
        _events.Add(DocumentEvent.Create(Id, eventType, Status, occurredAt, details));

    public void RecordClassification(
        string detectedType,
        decimal? confidence,
        DateTimeOffset occurredAt,
        string? details = null)
    {
        DetectedDocumentType = detectedType;
        ClassificationConfidence = confidence;
        _events.Add(DocumentEvent.Create(Id, DocumentEventTypes.Classified, DocumentStatus.Classifying, occurredAt, details));
    }

    /// <summary>
    /// The only way to reach COMPLETED: called once the whole result is persisted, so a partial
    /// result never shows as complete.
    /// </summary>
    public void MarkCompleted(DateTimeOffset occurredAt)
    {
        Status = DocumentStatus.Completed;
        CompletedAt = occurredAt;
        LastErrorCode = null;
        LastErrorMessage = null;
        _events.Add(DocumentEvent.Create(Id, DocumentEventTypes.Completed, DocumentStatus.Completed, occurredAt));
    }

    /// <summary>
    /// A transient failure with attempts left: the document goes back to the queue and keeps the
    /// error visible until a later attempt succeeds.
    /// </summary>
    public void MarkRetryScheduled(string errorCode, string errorMessage, DateTimeOffset occurredAt)
    {
        Status = DocumentStatus.Queued;
        LastErrorCode = errorCode;
        LastErrorMessage = errorMessage;
        _events.Add(DocumentEvent.Create(Id, DocumentEventTypes.RetryScheduled, DocumentStatus.Queued, occurredAt, errorCode));
    }

    /// <summary>
    /// The worker gave the document back without failing it, for instance on a clean shutdown.
    /// </summary>
    public void MarkRequeued(DateTimeOffset occurredAt, string details)
    {
        Status = DocumentStatus.Queued;
        _events.Add(DocumentEvent.Create(Id, DocumentEventTypes.Queued, DocumentStatus.Queued, occurredAt, details));
    }

    public void MarkFailed(string errorCode, string errorMessage, DateTimeOffset occurredAt)
    {
        Status = DocumentStatus.Failed;
        LastErrorCode = errorCode;
        LastErrorMessage = errorMessage;
        _events.Add(DocumentEvent.Create(Id, DocumentEventTypes.Failed, DocumentStatus.Failed, occurredAt, errorCode));
    }
}
