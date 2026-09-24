namespace DocReader.Application.Abstractions;

/// <summary>
/// Result of persisting a blob.
/// </summary>
/// <param name="StorageKey">Logical key, the only handle the rest of the system keeps.</param>
/// <param name="SizeBytes">Number of bytes written.</param>
public sealed record StoredFile(string StorageKey, long SizeBytes);
