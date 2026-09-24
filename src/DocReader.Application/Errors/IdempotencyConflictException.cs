namespace DocReader.Application.Errors;

/// <summary>
/// The same <c>Idempotency-Key</c> was replayed with a different file. Maps to 409.
/// </summary>
public sealed class IdempotencyConflictException(string key)
    : Exception("Idempotency-Key was already used with a different file.")
{
    public string Key { get; } = key;
}
