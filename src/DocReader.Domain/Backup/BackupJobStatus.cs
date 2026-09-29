namespace DocReader.Domain.Backup;

/// <summary>Lifecycle of one <see cref="BackupJob"/>.</summary>
public enum BackupJobStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3
}
