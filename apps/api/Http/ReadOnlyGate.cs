using DocReader.Application.Abstractions;

namespace DocReader.Api.Http;

/// <summary>
/// The read-only flag as the API sees it, re-read from the database at most once per <see cref="CacheDuration"/> so a
/// busy API does not query it on every request. A restore therefore takes effect within that long. If the flag cannot be
/// read (database down, schema not migrated) the gate stays open: the request will fail on its own anyway, with the
/// error that actually explains it.
/// </summary>
public sealed class ReadOnlyGate(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<ReadOnlyGate> logger)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private bool _isReadOnly;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<bool> IsReadOnlyAsync(CancellationToken ct)
    {
        if (timeProvider.GetUtcNow() < _expiresAt)
        {
            return _isReadOnly;
        }

        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (timeProvider.GetUtcNow() < _expiresAt)
            {
                return _isReadOnly;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<ISystemStateStore>();
                _isReadOnly = await store.IsReadOnlyAsync(ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Could not read the read-only flag; treating the system as writable. errorType={ErrorType}", exception.GetType().Name);
                _isReadOnly = false;
            }

            _expiresAt = timeProvider.GetUtcNow() + CacheDuration;

            return _isReadOnly;
        }
        finally
        {
            _refresh.Release();
        }
    }
}
