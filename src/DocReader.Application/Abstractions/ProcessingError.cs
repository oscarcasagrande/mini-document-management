namespace DocReader.Application.Abstractions;

/// <summary>
/// Failure reported by the worker. <paramref name="IsTransient"/> decides between a retry with
/// backoff and a definitive failure.
/// </summary>
public sealed record ProcessingError(string Code, string Message, bool IsTransient);
