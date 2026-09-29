using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DocReader.IntegrationTests;

/// <summary>Keeps every log entry in memory, so a test can check what was logged (and what was not).</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, formatter(state, exception), exception));
}
