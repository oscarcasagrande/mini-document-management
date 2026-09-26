namespace DocReader.Domain.Documents;

/// <summary>
/// Canonical event names written to the document timeline.
/// </summary>
public static class DocumentEventTypes
{
    public const string Received = "RECEIVED";
    public const string Stored = "STORED";
    public const string Queued = "QUEUED";
    public const string Rejected = "REJECTED";
    public const string Deleted = "DELETED";
    public const string Failed = "FAILED";
    public const string Purged = "PURGED";
    public const string RetentionApplied = "RETENTION_APPLIED";

    /// <summary>A webhook subscriber never accepted the notification of this document, after every retry.</summary>
    public const string WebhookDeliveryFailed = "WEBHOOK_DELIVERY_FAILED";

    public const string PreprocessingStarted = "PREPROCESSING_STARTED";
    public const string OcrStarted = "OCR_STARTED";
    public const string OcrPageCompleted = "OCR_PAGE_COMPLETED";
    public const string Classified = "CLASSIFIED";
    public const string ClassificationStarted = "CLASSIFICATION_STARTED";
    public const string ExtractionStarted = "EXTRACTION_STARTED";
    public const string Completed = "COMPLETED";
    public const string RetryScheduled = "RETRY_SCHEDULED";
}
