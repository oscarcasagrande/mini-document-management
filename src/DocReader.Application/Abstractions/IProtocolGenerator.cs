namespace DocReader.Application.Abstractions;

/// <summary>
/// Produces the next human readable protocol. The sequence is per day and must be safe under
/// concurrent uploads.
/// </summary>
public interface IProtocolGenerator
{
    Task<string> NextAsync(DateTimeOffset uploadedAt, CancellationToken ct);
}
