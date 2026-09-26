using DocReader.Application.Abstractions;

namespace DocReader.UnitTests.Fakes;

/// <summary>
/// Stand in for the storage facade: keeps blobs in a dictionary and records what was saved and deleted, and in which
/// repository, so the orphan cleanup path and the choice of repository can be asserted.
/// </summary>
public sealed class InMemoryFileStorage : IFileStorage
{
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    public List<string> DeletedKeys { get; } = [];

    /// <summary>The repository of every save, in order.</summary>
    public List<Guid> SavedIn { get; } = [];

    /// <summary>The repository of every delete, in order.</summary>
    public List<Guid> DeletedFrom { get; } = [];

    /// <summary>The repository of every read, in order.</summary>
    public List<Guid> ReadFrom { get; } = [];

    public IReadOnlyDictionary<string, byte[]> Blobs => _blobs;

    public async Task<StoredFile> SaveAsync(Guid repositoryId, Stream content, FileMetadata metadata, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        var key = $"documents/{metadata.CreatedAt:yyyy/MM/dd}/{metadata.DocumentId:D}/original{metadata.Extension}";
        _blobs[key] = bytes;
        SavedIn.Add(repositoryId);

        return new StoredFile(key, bytes.Length);
    }

    public Task<Stream> OpenReadAsync(Guid repositoryId, string storageKey, CancellationToken ct)
    {
        ReadFrom.Add(repositoryId);

        return _blobs.TryGetValue(storageKey, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false))
            : throw new FileNotFoundException("Stored original is not available.", storageKey);
    }

    public Task DeleteAsync(Guid repositoryId, string storageKey, CancellationToken ct)
    {
        DeletedKeys.Add(storageKey);
        DeletedFrom.Add(repositoryId);
        _blobs.Remove(storageKey);

        return Task.CompletedTask;
    }

    public Task<bool> IsWritableAsync(CancellationToken ct) => Task.FromResult(true);
}
