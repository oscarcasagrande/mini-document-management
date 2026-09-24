namespace DocReader.Application.Errors;

/// <summary>
/// The worker found out it no longer owns its job. Whatever it holds must be dropped: another
/// attempt is, or will be, responsible for the document.
/// </summary>
public sealed class JobLockLostException(Guid jobId)
    : Exception("The processing job was taken over by another attempt.")
{
    public Guid JobId { get; } = jobId;
}
