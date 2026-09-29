using System.Text.Json.Nodes;
using Azure;
using DocReader.Application.Abstractions;
using DocReader.Infrastructure.Storage;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// <see cref="AzureBlobStorageAdapter"/> against a real Azurite (the official Azure Storage emulator), reachable at
/// the default <c>UseDevelopmentStorage=true</c> endpoint. Skipped, not failed, when Azurite is not running:
///
///   docker run --rm -p 10000:10000 mcr.microsoft.com/azure-storage/azurite
///
/// The AWS S3 adapter shares the same construction, key-layout and error-mapping code paths (see
/// <see cref="StorageKeyLayout"/> and the two adapters' <c>RequireString</c>/error handling); this suite is what
/// stands in for a live cloud round trip for both, since a real S3-compatible emulator was not reachable from this
/// environment's container registry allowlist. The unit tests in <c>StorageInfrastructureTests</c> cover
/// construction/validation for both providers without a live service.
/// </summary>
public sealed class CloudStorageAdapterTests : IAsyncLifetime
{
    private const string AzuriteConnectionString = "UseDevelopmentStorage=true";
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private bool _reachable;

    public async ValueTask InitializeAsync()
    {
        try
        {
            var adapter = Adapter();
            _reachable = await adapter.IsWritableAsync(Ct);
        }
        catch (Exception exception) when (exception is RequestFailedException or InvalidOperationException or AggregateException)
        {
            _reachable = false;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static AzureBlobStorageAdapter Adapter() => new(new JsonObject
    {
        ["connectionString"] = AzuriteConnectionString,
        ["container"] = $"docreader-tests-{Guid.NewGuid():N}"
    });

    [Fact]
    public async Task Upload_download_e_exclusao_de_1_MB_funcionam_de_ponta_a_ponta()
    {
        Assert.SkipUnless(_reachable, "Azurite não alcançável em UseDevelopmentStorage=true. Suba com: docker run --rm -p 10000:10000 mcr.microsoft.com/azure-storage/azurite");

        var adapter = Adapter();
        var documentId = Guid.CreateVersion7(Now);
        var metadata = new FileMetadata(documentId, ".pdf", "application/pdf", Now);
        var bytes = new byte[1024 * 1024];
        Random.Shared.NextBytes(bytes);

        StoredFile stored;
        await using (var content = new MemoryStream(bytes))
        {
            stored = await adapter.SaveAsync(content, metadata, Ct);
        }

        Assert.Equal(bytes.Length, stored.SizeBytes);
        Assert.Contains(documentId.ToString("D"), stored.StorageKey, StringComparison.Ordinal);

        await using (var downloaded = await adapter.OpenReadAsync(stored.StorageKey, Ct))
        await using (var buffer = new MemoryStream())
        {
            await downloaded.CopyToAsync(buffer, Ct);
            Assert.Equal(bytes, buffer.ToArray());
        }

        await adapter.DeleteAsync(stored.StorageKey, Ct);

        await Assert.ThrowsAsync<FileNotFoundException>(() => adapter.OpenReadAsync(stored.StorageKey, Ct));
    }

    [Fact]
    public async Task Excluir_uma_chave_que_ja_nao_existe_nao_e_erro()
    {
        Assert.SkipUnless(_reachable, "Azurite não alcançável em UseDevelopmentStorage=true. Suba com: docker run --rm -p 10000:10000 mcr.microsoft.com/azure-storage/azurite");

        var adapter = Adapter();

        await adapter.DeleteAsync("documents/2026/09/29/does-not-exist/original.pdf", Ct);
    }

    [Fact]
    public async Task Ler_uma_chave_inexistente_lanca_FileNotFoundException()
    {
        Assert.SkipUnless(_reachable, "Azurite não alcançável em UseDevelopmentStorage=true. Suba com: docker run --rm -p 10000:10000 mcr.microsoft.com/azure-storage/azurite");

        var adapter = Adapter();

        await Assert.ThrowsAsync<FileNotFoundException>(() => adapter.OpenReadAsync("documents/nope/original.pdf", Ct));
    }
}
