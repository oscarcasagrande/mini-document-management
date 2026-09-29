namespace DocReader.Domain.Retention;

/// <summary>Lifecycle of one <see cref="RetentionReapplyRequest"/> run.</summary>
public enum RetentionReapplyStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3
}
