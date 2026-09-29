namespace DocReader.Domain.Backup;

/// <summary>Lifecycle of one <see cref="RestoreJob"/>.</summary>
public enum RestoreJobStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3
}
