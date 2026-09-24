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

    /// <summary>MIME type detected from the file signature, not the declared one.</summary>
    public string MimeType { get; private init; } = string.Empty;

    public long SizeBytes { get; private init; }

    public string Sha256 { get; private init; } = string.Empty;

    public int PageCount { get; private init; }

    public UploadChannel UploadChannel { get; private init; }

    public string? ExternalReference { get; private init; }

    public string? ExpectedDocumentType { get; private init; }

    public string? DetectedDocumentType { get; private set; }

    public decimal? ClassificationConfidence { get; private set; }

    public DocumentStatus Status { get; private set; }

    public DateTimeOffset UploadedAt { get; private init; }

    public DateTimeOffset? CompletedAt { get; private set; }

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
        DateTimeOffset uploadedAt)
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
            MimeType = mimeType,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            PageCount = pageCount,
            UploadChannel = uploadChannel,
            ExternalReference = externalReference,
            ExpectedDocumentType = expectedDocumentType,
            UploadedAt = uploadedAt,
            Status = DocumentStatus.Received
        };

        document._events.Add(DocumentEvent.Create(id, DocumentEventTypes.Received, DocumentStatus.Received, uploadedAt));
        document.Status = DocumentStatus.Stored;
        document._events.Add(DocumentEvent.Create(id, DocumentEventTypes.Stored, DocumentStatus.Stored, uploadedAt));

        return document;
    }

    /// <summary>
    /// Marks the document as queued for the worker. Called in the same transaction that persists the job.
    /// </summary>
    public void MarkQueued(DateTimeOffset occurredAt)
    {
        if (Status is not (DocumentStatus.Stored or DocumentStatus.Failed or DocumentStatus.Completed))
        {
            throw new InvalidOperationException($"Cannot queue a document in status {Status}.");
        }

        Status = DocumentStatus.Queued;
        LastErrorCode = null;
        LastErrorMessage = null;
        _events.Add(DocumentEvent.Create(Id, DocumentEventTypes.Queued, DocumentStatus.Queued, occurredAt));
    }

    public void MarkFailed(string errorCode, string errorMessage, DateTimeOffset occurredAt)
    {
        Status = DocumentStatus.Failed;
        LastErrorCode = errorCode;
        LastErrorMessage = errorMessage;
        _events.Add(DocumentEvent.Create(Id, DocumentEventTypes.Failed, DocumentStatus.Failed, occurredAt, errorCode));
    }
}
