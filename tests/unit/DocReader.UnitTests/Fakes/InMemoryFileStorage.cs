using DocReader.Application.Abstractions;

namespace DocReader.UnitTests.Fakes;

/// <summary>
/// Stand in for the volume: keeps blobs in a dictionary and records what was deleted, so the orphan
/// cleanup path can be asserted.
/// </summary>
public sealed class InMemoryFileStorage : IFileStorage
{
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    public List<string> DeletedKeys { get; } = [];

    public IReadOnlyDictionary<string, byte[]> Blobs => _blobs;

    public async Task<StoredFile> SaveAsync(Stream content, FileMetadata metadata, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        var key = $"documents/{metadata.CreatedAt:yyyy/MM/dd}/{metadata.DocumentId:D}/original{metadata.Extension}";
        _blobs[key] = bytes;

        return new StoredFile(key, bytes.Length);
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct) =>
        _blobs.TryGetValue(storageKey, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : throw new FileNotFoundException("Stored original is not available.", storageKey);

    public Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        DeletedKeys.Add(storageKey);
        _blobs.Remove(storageKey);

        return Task.CompletedTask;
    }

    public Task<bool> IsWritableAsync(CancellationToken ct) => Task.FromResult(true);
}
