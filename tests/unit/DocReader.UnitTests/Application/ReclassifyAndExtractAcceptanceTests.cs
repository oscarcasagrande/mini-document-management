using DocReader.Application.Abstractions;
using DocReader.Application.Classification;
using DocReader.Application.Extraction;
using DocReader.Application.Options;
using DocReader.Application.Processing;
using DocReader.Application.Retention;
using DocReader.Domain.Catalog;
using DocReader.Domain.Documents;
using DocReader.Domain.Processing;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>
/// Acceptance test of "Configuração Dinâmica" item 3: a document reclassified against the current
/// document-type rules can come out under a different type than the one originally recorded, when an
/// administrator makes a competing rule more specific. This exercises the actual mechanism the
/// <c>PUT /api/v1/documents/{id}/reclassify-and-extract</c> trigger relies on: <see cref="DynamicDocumentClassifier"/>
/// reads the <see cref="DocumentType"/> rows fresh on every run of the pipeline, so editing a row between
/// two runs of <see cref="DocumentProcessor.ProcessAsync"/> changes what the next run decides.
/// </summary>
public sealed class ReclassifyAndExtractAcceptanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private const string SharedText = "SPECIAL DOCUMENT TITLE\nEXTRA DETAIL LINE";

    [Fact]
    public async Task Reclassificacao_troca_o_tipo_gravado_quando_uma_regra_mais_especifica_passa_a_pontuar_mais()
    {
        var typeStore = new InMemoryDocumentTypeStore();

        var typeA = DocumentType.Create(
            Guid.CreateVersion7(Now),
            "TEST_TYPE_A",
            "Test Type A",
            "{}",
            """{"evidence":[{"name":"generic","weight":0.5,"patterns":["SPECIAL DOCUMENT"]}],"threshold":0.4}""",
            "{}",
            active: true,
            isBuiltIn: false,
            Now);

        var typeB = DocumentType.Create(
            Guid.CreateVersion7(Now.AddSeconds(1)),
            "TEST_TYPE_B",
            "Test Type B",
            "{}",
            """{"evidence":[{"name":"specific","weight":0.3,"patterns":["EXTRA DETAIL LINE"]}],"threshold":0.9}""",
            "{}",
            active: true,
            isBuiltIn: false,
            Now);

        typeStore.Items.Add(typeA);
        typeStore.Items.Add(typeB);

        var document = NewDocument();
        var store = new FakeProcessingStore(document);
        var time = new FakeTimeProvider(Now);

        var processor = new DocumentProcessor(
            new FakeProcessingQueue(),
            store,
            new StubStorage(),
            ScriptedOcrProvider.ReadingPages([SharedText.Split('\n')]),
            new DynamicDocumentClassifier(typeStore, Options.Create(new ClassificationOptions())),
            [],
            new RetentionService(new InMemoryRetentionPolicyStore()),
            Options.Create(new ProcessingQueueOptions()),
            Options.Create(new OcrProviderOptions()),
            time,
            NullLogger<DocumentProcessor>.Instance);

        // First run: A is the only type that reaches its threshold.
        await processor.ProcessAsync(
            ProcessingJob.CreateForDocument(document.Id, Now),
            TestContext.Current.CancellationToken);

        Assert.Equal("TEST_TYPE_A", Assert.Single(store.Completed).DetectedType);

        // An administrator makes B's rule more specific and stronger than A's, simulating an edit through
        // PUT /api/v1/document-types/{id}. The classifier picks this up on the very next run: no restart,
        // no redeploy, exactly what makes /reclassify-and-extract meaningful.
        typeB.Update(
            typeB.Name,
            typeB.SchemaJson,
            """{"evidence":[{"name":"specific","weight":0.9,"patterns":["EXTRA DETAIL LINE"]}],"threshold":0.5}""",
            typeB.ExtractionRulesJson,
            active: true,
            Now.AddMinutes(1));

        // Second run: the reclassification trigger's effect, simulated by running the pipeline again
        // against the same document with a fresh job, exactly as the worker would after
        // ReclassifyAndExtractAsync queues one.
        await processor.ProcessAsync(
            ProcessingJob.CreateForDocument(document.Id, Now.AddMinutes(1)),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, store.Completed.Count);
        Assert.Equal("TEST_TYPE_B", store.Completed[1].DetectedType);
    }

    private static Document NewDocument()
    {
        var id = Guid.CreateVersion7(Now);
        var document = Document.Accept(
            id,
            "DOC-20260928-000007",
            "documento.png",
            $"documents/2026/09/28/{id:D}/original.png",
            "image/png",
            2048,
            new string('d', 64),
            1,
            UploadChannel.Api,
            null,
            null,
            Now);

        document.MarkQueued(Now);
        return document;
    }

    /// <summary>Storage that serves a few fixed bytes for any key.</summary>
    private sealed class StubStorage : IFileStorage
    {
        public Task<StoredFile> SaveAsync(Guid repositoryId, Stream content, FileMetadata metadata, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(Guid repositoryId, string storageKey, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));

        public Task DeleteAsync(Guid repositoryId, string storageKey, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> IsWritableAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
