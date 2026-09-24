namespace DocReader.Infrastructure.Persistence;

/// <summary>
/// Daily counter behind the human readable protocol. It only exists so the migration creates the
/// table; the value is allocated by a single atomic upsert, never by reading and writing.
/// </summary>
public sealed class ProtocolSequence
{
    public DateOnly Day { get; set; }

    public long LastNumber { get; set; }
}
