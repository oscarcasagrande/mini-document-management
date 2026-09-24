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
    Rejected = 9
}
