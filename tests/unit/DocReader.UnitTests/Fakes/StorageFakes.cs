using System.Text;
using System.Text.Json.Nodes;
using DocReader.Application.Abstractions;
using DocReader.Domain.Storage;

namespace DocReader.UnitTests.Fakes;

/// <summary>Reversible and readable, so a test can prove the stored text is not the plain settings.</summary>
public sealed class FakeSecretProtector : ISecretProtector
{
    public const string Prefix = "{\"fake\":\"";

    public string Protect(string plainJson) => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainJson)) + "\"}";

    public string Unprotect(string protectedJson)
    {
        if (!protectedJson.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("not protected by this key");
        }

        var encoded = protectedJson[Prefix.Length..^2];

        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }
}

/// <summary>An adapter that keeps its blobs in memory and remembers the settings it was built with.</summary>
public sealed class RecordingAdapter(StorageProvider provider, JsonObject? config, Dictionary<string, byte[]> blobs) : IStorageAdapter
{
    private readonly Dictionary<string, byte[]> _blobs = blobs;

    public StorageProvider Provider { get; } = provider;

    public JsonObject? Config { get; } = config;

    public IReadOnlyDictionary<string, byte[]> Blobs => _blobs;

    public bool Writable { get; set; } = true;

    public async Task<StoredFile> SaveAsync(Stream content, FileMetadata metadata, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var key = $"{Provider}/{metadata.DocumentId:D}";
        _blobs[key] = buffer.ToArray();

        return new StoredFile(key, buffer.Length);
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct) =>
        _blobs.TryGetValue(storageKey, out var bytes)
            ? Task.FromResult<Stream>(new MemoryStream(bytes))
            : throw new FileNotFoundException("missing", storageKey);

    public Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        _blobs.Remove(storageKey);

        return Task.CompletedTask;
    }

    public Task<bool> IsWritableAsync(CancellationToken ct) => Task.FromResult(Writable);
}

/// <summary>Builds <see cref="RecordingAdapter"/>s and keeps them, so a test can see which one served a call.</summary>
public sealed class RecordingAdapterFactory : IStorageAdapterFactory
{
    private readonly Dictionary<string, Dictionary<string, byte[]>> _stores = new(StringComparer.Ordinal);

    public List<RecordingAdapter> Created { get; } = [];

    public IStorageAdapter Create(StorageProvider provider, JsonObject? connectionConfig)
    {
        // One store per provider and settings, like a real repository: a later call sees what an earlier one saved.
        var storeKey = provider + "|" + connectionConfig?.ToJsonString();
        if (!_stores.TryGetValue(storeKey, out var blobs))
        {
            blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            _stores[storeKey] = blobs;
        }

        var adapter = new RecordingAdapter(provider, connectionConfig?.DeepClone() as JsonObject, blobs);
        Created.Add(adapter);

        return adapter;
    }
}
