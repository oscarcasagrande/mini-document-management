using DocReader.Application.Abstractions;
using DocReader.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DocReader.UnitTests.Infrastructure;

public sealed class FileSystemStorageAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "docreader-tests",
        Guid.NewGuid().ToString("N"));

    private readonly FileSystemStorageAdapter _storage;

    public FileSystemStorageAdapterTests()
    {
        _storage = new FileSystemStorageAdapter(_root, NullLogger<FileSystemStorageAdapter>.Instance);
    }

    [Fact]
    public async Task Builds_the_key_from_the_document_id_and_never_from_the_file_name()
    {
        var documentId = Guid.Parse("0199c1f0-7b3a-7a10-9c44-2f1d8e6b4a21");
        var metadata = new FileMetadata(
            documentId,
            ".pdf",
            "application/pdf",
            new DateTimeOffset(2026, 9, 24, 22, 0, 0, TimeSpan.Zero));

        await using var content = new MemoryStream("hello"u8.ToArray());
        var stored = await _storage.SaveAsync(content, metadata, TestContext.Current.CancellationToken);

        Assert.Equal(
            $"documents/2026/09/24/{documentId:D}/original.pdf",
            stored.StorageKey);
        Assert.Equal(5, stored.SizeBytes);
    }

    [Fact]
    public async Task Round_trips_the_stored_bytes()
    {
        var payload = Samples.BytesOf(Samples.OnePagePdf);
        var metadata = NewMetadata();

        await using var source = new MemoryStream(payload);
        var stored = await _storage.SaveAsync(source, metadata, TestContext.Current.CancellationToken);

        await using var read = await _storage.OpenReadAsync(stored.StorageKey, TestContext.Current.CancellationToken);
        await using var buffer = new MemoryStream();
        await read.CopyToAsync(buffer, TestContext.Current.CancellationToken);

        Assert.Equal(payload, buffer.ToArray());
    }

    [Fact]
    public async Task Deletes_the_blob_and_prunes_the_empty_folders()
    {
        var metadata = NewMetadata();
        await using var content = new MemoryStream("data"u8.ToArray());
        var stored = await _storage.SaveAsync(content, metadata, TestContext.Current.CancellationToken);

        await _storage.DeleteAsync(stored.StorageKey, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _storage.OpenReadAsync(stored.StorageKey, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(_root, "documents", "2026")));
    }

    [Fact]
    public async Task Deleting_something_already_gone_is_not_an_error()
    {
        var key = $"documents/2026/09/24/{Guid.NewGuid():D}/original.pdf";

        await _storage.DeleteAsync(key, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("documents/../../escape/original.pdf")]
    [InlineData("/etc/passwd")]
    [InlineData("documents\\2026\\original.pdf")]
    [InlineData("")]
    public async Task Refuses_a_key_that_could_escape_the_root(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _storage.OpenReadAsync(key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Refuses_an_extension_that_is_not_a_plain_suffix()
    {
        var metadata = new FileMetadata(
            Guid.NewGuid(),
            "./../evil",
            "application/pdf",
            DateTimeOffset.UtcNow);

        await using var content = new MemoryStream("data"u8.ToArray());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _storage.SaveAsync(content, metadata, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reports_the_volume_as_writable()
    {
        Assert.True(await _storage.IsWritableAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Leaves_no_partial_file_behind()
    {
        var metadata = NewMetadata();
        await using var content = new MemoryStream("data"u8.ToArray());
        await _storage.SaveAsync(content, metadata, TestContext.Current.CancellationToken);

        var partials = Directory.EnumerateFiles(_root, "*.part", SearchOption.AllDirectories);

        Assert.Empty(partials);
    }

    private static FileMetadata NewMetadata() => new(
        Guid.NewGuid(),
        ".pdf",
        "application/pdf",
        new DateTimeOffset(2026, 9, 24, 22, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
