using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Application.Options;
using DocReader.Domain.Documents;
using DocReader.Infrastructure.Files;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

public sealed class DocumentUploadServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 22, 0, 0, TimeSpan.Zero);

    private readonly InMemoryDocumentStore _store = new() { Now = Now };
    private readonly InMemoryFileStorage _storage = new();
    private readonly UploadOptions _upload = new();
    private readonly IdempotencyOptions _idempotency = new();

    [Fact]
    public async Task Accepts_a_pdf_and_queues_it_with_a_protocol()
    {
        var service = BuildService();
        await using var content = Samples.StreamOf(Samples.ThreePagePdf);

        var result = await service.UploadAsync(
            CommandFor(content, "contrato.pdf", "application/pdf"),
            TestContext.Current.CancellationToken);

        Assert.Equal("DOC-20260924-000001", result.Protocol);
        Assert.Equal(DocumentStatus.Queued, result.Status);
        Assert.False(result.Replayed);

        var document = Assert.Single(_store.Documents);
        Assert.Equal("contrato.pdf", document.OriginalFileName);
        Assert.Equal("application/pdf", document.MimeType);
        Assert.Equal(3, document.PageCount);
        Assert.Equal(64, document.Sha256.Length);
        Assert.Equal(UploadChannel.Api, document.UploadChannel);
        Assert.Single(_store.Jobs);
        Assert.Single(_storage.Blobs);
    }

    [Fact]
    public async Task Writes_the_full_timeline_of_the_acceptance()
    {
        var service = BuildService();
        await using var content = Samples.StreamOf(Samples.OnePagePdf);

        await service.UploadAsync(CommandFor(content), TestContext.Current.CancellationToken);

        var document = Assert.Single(_store.Documents);
        Assert.Equal(
            new[] { "RECEIVED", "STORED", "QUEUED" },
            document.Events.Select(documentEvent => documentEvent.EventType).ToArray());
    }

    [Fact]
    public async Task Stores_the_blob_under_a_key_derived_from_the_document_id()
    {
        var service = BuildService();
        await using var content = Samples.StreamOf(Samples.OnePagePdf);

        await service.UploadAsync(
            CommandFor(content, "../../etc/passwd.pdf"),
            TestContext.Current.CancellationToken);

        var document = Assert.Single(_store.Documents);
        Assert.Equal($"documents/2026/09/24/{document.Id:D}/original.pdf", document.StorageKey);
        Assert.Equal("passwd.pdf", document.OriginalFileName);
    }

    [Fact]
    public async Task Refuses_a_file_above_the_size_limit_with_the_413_reason()
    {
        _upload.MaxSizeBytes = 256;
        var service = BuildService();
        await using var content = Samples.StreamOf(Samples.ThreePagePdf);

        var exception = await Assert.ThrowsAsync<UploadRejectedException>(() =>
            service.UploadAsync(CommandFor(content), TestContext.Current.CancellationToken));

        Assert.Equal(UploadRejectionReason.TooLarge, exception.Reason);
        Assert.Empty(_storage.Blobs);
        Assert.Empty(_store.Documents);
    }

    [Fact]
    public async Task Refuses_an_executable_wearing_a_pdf_name_with_the_415_reason()
    {
        var service = BuildService();
        await using var content = Samples.StreamOf(Samples.DisguisedExecutable);

        var exception = await Assert.ThrowsAsync<UploadRejectedException>(() =>
            service.UploadAsync(
                CommandFor(content, "documento.pdf", "application/pdf"),
                TestContext.Current.CancellationToken));

        Assert.Equal(UploadRejectionReason.BlockedContent, exception.Reason);
        Assert.Empty(_storage.Blobs);
    }

    [Fact]
    public async Task Refuses_a_declared_type_that_contradicts_the_signature()
    {
        var service = BuildService();
        await using var content = Samples.StreamOf(Samples.OnePagePdf);

        var exception = await Assert.ThrowsAsync<UploadRejectedException>(() =>
            service.UploadAsync(
                CommandFor(content, "documento.png", "image/png"),
                TestContext.Current.CancellationToken));

        Assert.Equal(UploadRejectionReason.DeclaredTypeMismatch, exception.Reason);
    }

    [Fact]
    public async Task Refuses_a_document_above_the_page_limit_with_the_422_reason()
    {
        _upload.MaxPageCount = 50;
        var service = BuildService();
        await using var content = Samples.StreamOf(Samples.SixtyPagePdf);

        var exception = await Assert.ThrowsAsync<UploadRejectedException>(() =>
            service.UploadAsync(CommandFor(content), TestContext.Current.CancellationToken));

        Assert.Equal(UploadRejectionReason.UnprocessableContent, exception.Reason);
        Assert.Equal("PAGE_LIMIT_EXCEEDED", exception.ErrorCode);
        Assert.Empty(_storage.Blobs);
    }

    [Fact]
    public async Task Refuses_a_corrupted_file_of_an_accepted_format_with_the_422_reason()
    {
        var service = BuildService();
        await using var content = Samples.StreamOf(Samples.CorruptedPdf);

        var exception = await Assert.ThrowsAsync<UploadRejectedException>(() =>
            service.UploadAsync(CommandFor(content), TestContext.Current.CancellationToken));

        Assert.Equal(UploadRejectionReason.UnprocessableContent, exception.Reason);
        Assert.Equal("UNREADABLE_DOCUMENT", exception.ErrorCode);
    }

    [Fact]
    public async Task Refuses_an_empty_file()
    {
        var service = BuildService();
        await using var content = new MemoryStream();

        var exception = await Assert.ThrowsAsync<UploadRejectedException>(() =>
            service.UploadAsync(CommandFor(content), TestContext.Current.CancellationToken));

        Assert.Equal(UploadRejectionReason.MissingFile, exception.Reason);
    }

    [Fact]
    public async Task Replays_the_original_response_for_the_same_key_and_the_same_file()
    {
        var service = BuildService();

        await using var first = Samples.StreamOf(Samples.OnePagePdf);
        var original = await service.UploadAsync(
            CommandFor(first, idempotencyKey: "key-1"),
            TestContext.Current.CancellationToken);

        await using var second = Samples.StreamOf(Samples.OnePagePdf);
        var replay = await service.UploadAsync(
            CommandFor(second, idempotencyKey: "key-1"),
            TestContext.Current.CancellationToken);

        Assert.True(replay.Replayed);
        Assert.Equal(original.DocumentId, replay.DocumentId);
        Assert.Equal(original.Protocol, replay.Protocol);
        Assert.Single(_store.Documents);
        Assert.Single(_store.Jobs);
        Assert.Single(_storage.Blobs);
    }

    [Fact]
    public async Task Answers_a_conflict_for_the_same_key_with_a_different_file()
    {
        var service = BuildService();

        await using var first = Samples.StreamOf(Samples.OnePagePdf);
        await service.UploadAsync(
            CommandFor(first, idempotencyKey: "key-1"),
            TestContext.Current.CancellationToken);

        await using var second = Samples.StreamOf(Samples.ThreePagePdf);
        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            service.UploadAsync(
                CommandFor(second, idempotencyKey: "key-1"),
                TestContext.Current.CancellationToken));

        Assert.Single(_store.Documents);
        Assert.Single(_storage.Blobs);
    }

    [Fact]
    public async Task Frees_the_key_once_the_ttl_expired()
    {
        _idempotency.Ttl = TimeSpan.FromHours(1);
        var service = BuildService();

        await using var first = Samples.StreamOf(Samples.OnePagePdf);
        var original = await service.UploadAsync(
            CommandFor(first, idempotencyKey: "key-1"),
            TestContext.Current.CancellationToken);

        _store.Now = Now.AddHours(2);

        await using var second = Samples.StreamOf(Samples.OnePagePdf);
        var secondResult = await service.UploadAsync(
            CommandFor(second, idempotencyKey: "key-1"),
            TestContext.Current.CancellationToken);

        Assert.False(secondResult.Replayed);
        Assert.NotEqual(original.DocumentId, secondResult.DocumentId);
        Assert.Equal(2, _store.Documents.Count);
    }

    [Fact]
    public async Task Without_the_header_two_identical_uploads_produce_two_documents()
    {
        var service = BuildService();

        await using var first = Samples.StreamOf(Samples.OnePagePdf);
        await service.UploadAsync(CommandFor(first), TestContext.Current.CancellationToken);

        await using var second = Samples.StreamOf(Samples.OnePagePdf);
        await service.UploadAsync(CommandFor(second), TestContext.Current.CancellationToken);

        Assert.Equal(2, _store.Documents.Count);
        Assert.Equal(2, _store.Jobs.Count);
    }

    [Fact]
    public async Task Removes_the_orphan_blob_when_persistence_fails()
    {
        _store.AcceptFailure = new InvalidOperationException("database is down");
        var service = BuildService();
        await using var content = Samples.StreamOf(Samples.OnePagePdf);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadAsync(CommandFor(content), TestContext.Current.CancellationToken));

        Assert.Empty(_store.Documents);
        Assert.Single(_storage.DeletedKeys);
        Assert.Empty(_storage.Blobs);
    }

    [Fact]
    public async Task Refuses_an_oversized_idempotency_key()
    {
        _idempotency.MaxKeyLength = 8;
        var service = BuildService();
        await using var content = Samples.StreamOf(Samples.OnePagePdf);

        var exception = await Assert.ThrowsAsync<UploadRejectedException>(() =>
            service.UploadAsync(
                CommandFor(content, idempotencyKey: new string('k', 9)),
                TestContext.Current.CancellationToken));

        Assert.Equal(UploadRejectionReason.InvalidRequest, exception.Reason);
    }

    [Fact]
    public async Task Records_the_channel_reported_by_the_interface()
    {
        var service = BuildService();
        await using var content = Samples.StreamOf(Samples.Png);

        await service.UploadAsync(
            new UploadDocumentCommand(
                content,
                "digitalizado.png",
                "image/png",
                "BR_RG",
                "CLIENTE-123",
                UploadChannel.Web,
                null),
            TestContext.Current.CancellationToken);

        var document = Assert.Single(_store.Documents);
        Assert.Equal(UploadChannel.Web, document.UploadChannel);
        Assert.Equal("BR_RG", document.ExpectedDocumentType);
        Assert.Equal("CLIENTE-123", document.ExternalReference);
        Assert.Equal("image/png", document.MimeType);
        Assert.Equal(1, document.PageCount);
    }

    private DocumentUploadService BuildService() => new(
        _storage,
        _store,
        _store,
        new SequentialProtocolGenerator(),
        new DocumentPageCounter(),
        Options.Create(_upload),
        Options.Create(_idempotency),
        new FakeTimeProvider(Now),
        NullLogger<DocumentUploadService>.Instance);

    private static UploadDocumentCommand CommandFor(
        Stream content,
        string fileName = "documento.pdf",
        string? contentType = "application/pdf",
        string? idempotencyKey = null) =>
        new(content, fileName, contentType, null, null, UploadChannel.Api, idempotencyKey);
}
