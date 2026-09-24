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
}
