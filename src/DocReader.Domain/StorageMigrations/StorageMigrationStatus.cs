namespace DocReader.Domain.StorageMigrations;

/// <summary>Lifecycle of one <see cref="StorageMigrationJob"/> run.</summary>
public enum StorageMigrationStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,

    /// <summary>Stopped on request before it finished. Documents already moved stay where they were moved to.</summary>
    Cancelled = 4
}
