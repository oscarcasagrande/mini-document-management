namespace DocReader.Domain.GdprDeletion;

/// <summary>Lifecycle of one <see cref="GdprDeletionRequest"/>.</summary>
public enum GdprDeletionRequestStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Executed = 3
}
