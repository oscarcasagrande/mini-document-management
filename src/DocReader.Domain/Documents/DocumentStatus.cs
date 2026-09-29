namespace DocReader.Domain.Documents;

/// <summary>
/// Lifecycle of a document, as defined by RF-007 of the PRD.
/// </summary>
public enum DocumentStatus
{
    Received = 0,
    Stored = 1,
    Queued = 2,
    Preprocessing = 3,
    OcrRunning = 4,
    Classifying = 5,
    Extracting = 6,
    Completed = 7,
    Failed = 8,
    Rejected = 9,

    /// <summary>
    /// The original file, the OCR text and the extracted fields were removed, either because the retention period
    /// ended or because a GDPR/LGPD deletion request was executed. The timeline says which (<c>PURGED</c> vs.
    /// <c>GDPR_DELETION_EXECUTED</c>); the metadata and the history stay either way.
    /// </summary>
    Purged = 10
}
