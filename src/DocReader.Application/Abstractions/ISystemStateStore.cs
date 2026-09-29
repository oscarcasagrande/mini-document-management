namespace DocReader.Application.Abstractions;

/// <summary>The installation-wide read-only gate, kept in one row of the database so the API and the worker agree on it.</summary>
public interface ISystemStateStore
{
    Task<bool> IsReadOnlyAsync(CancellationToken ct);

    Task SetReadOnlyAsync(bool readOnly, DateTimeOffset now, CancellationToken ct);
}
