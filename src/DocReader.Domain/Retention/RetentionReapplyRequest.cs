namespace DocReader.Domain.Retention;

/// <summary>
/// One request to recalculate <c>expiresAt</c> on every document that currently carries a given
/// <see cref="RetentionPolicy"/>, after its <see cref="RetentionPolicy.RetentionDays"/> changed. The worker claims
/// a pending request and works through the affected documents in batches; this row tracks that run.
/// </summary>
public sealed class RetentionReapplyRequest
{
    private RetentionReapplyRequest()
    {
    }

    public Guid Id { get; private init; }

    public Guid RetentionPolicyId { get; private init; }

    public RetentionReapplyStatus Status { get; private set; }

    public DateTimeOffset RequestedAt { get; private init; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public int DocumentsUpdated { get; private set; }

    public string? ErrorMessage { get; private set; }

    public static RetentionReapplyRequest Create(Guid id, Guid retentionPolicyId, DateTimeOffset now) => new()
    {
        Id = id,
        RetentionPolicyId = retentionPolicyId,
        Status = RetentionReapplyStatus.Pending,
        RequestedAt = now
    };

    public void MarkRunning(DateTimeOffset now)
    {
        Status = RetentionReapplyStatus.Running;
        StartedAt = now;
    }

    public void MarkCompleted(DateTimeOffset now, int documentsUpdated)
    {
        Status = RetentionReapplyStatus.Completed;
        CompletedAt = now;
        DocumentsUpdated = documentsUpdated;
    }

    public void MarkFailed(DateTimeOffset now, string errorMessage)
    {
        Status = RetentionReapplyStatus.Failed;
        CompletedAt = now;
        ErrorMessage = errorMessage;
    }
}
