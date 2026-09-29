namespace DocReader.Domain.Backup;

/// <summary>
/// Installation-wide switches, as a single row with a fixed id (seeded by the migration). Today it holds only the
/// read-only gate a restore raises while it replaces the database: the API refuses writes while it is on.
/// </summary>
public sealed class SystemState
{
    /// <summary>Identity of the one row the migration creates.</summary>
    public static readonly Guid SingletonId = new("00000000-0000-7000-8000-0000000005a1");

    private SystemState()
    {
    }

    public Guid Id { get; private init; }

    public bool IsReadOnly { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}
