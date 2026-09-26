using DocReader.Application.Retention;
using DocReader.Application.Classification;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Application.Extraction;
using DocReader.Domain.Documents;
using DocReader.Domain.Processing;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>
/// The read side of the result and the reprocessing rules of RF-013, as the API services see them.
/// </summary>
public sealed class ResultQueriesAndReprocessTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly ExtractionSummary Summary = new(
        Guid.NewGuid(), "paddleocr", "PP-OCRv5 test", "rules-1.0.0", "br-cpf-card-1.0.0", 1, 0.99m, Now);

    private static (InMemoryDocumentStore Store, Document Document) StoreWithDocument(
        DocumentStatus status = DocumentStatus.Queued,
        bool withJob = true)
    {
        var store = new InMemoryDocumentStore();
        var id = Guid.CreateVersion7(Now);

        var document = Document.Accept(
            id, "DOC-20260924-000003", "cartao.png", $"documents/{id:D}/original.png", "image/png",
            2048, new string('c', 64), 1, UploadChannel.Api, null, null, Now);
        document.MarkQueued(Now);

        var job = ProcessingJob.CreateForDocument(id, Now);
        store.AcceptAsync(document, job, null, CancellationToken.None).GetAwaiter().GetResult();

        if (!withJob)
        {
            store.Jobs.Clear();
        }

        // Status is driven through the domain methods, the way the worker drives it.
        switch (status)
        {
            case DocumentStatus.Completed:
                document.MarkCompleted(Now);
                store.Jobs.Clear();
                break;
            case DocumentStatus.Failed:
                document.MarkFailed("OCR_UNAVAILABLE", "down", Now);
                store.Jobs.Clear();
                break;
        }

        return (store, document);
    }

    private static DocumentQueryService QueryService(InMemoryDocumentStore store) =>
        new(
            store,
            new InMemoryFileStorage(),
            new RulesDocumentClassifier(),
            [new BrCnhExtractor(new FakeTimeProvider(Now)), new BrCinExtractor(new FakeTimeProvider(Now))],
            NullLogger<DocumentQueryService>.Instance);

    private static DocumentReprocessingService ReprocessService(InMemoryDocumentStore store) =>
        new(store, new RetentionService(new InMemoryRetentionPolicyStore()), new FakeTimeProvider(Now.AddHours(1)), NullLogger<DocumentReprocessingService>.Instance);

    [Fact]
    public async Task Resultado_de_documento_ainda_na_fila_e_conflito_com_o_status_atual()
    {
        var (store, document) = StoreWithDocument(DocumentStatus.Queued);

        var error = await Assert.ThrowsAsync<ResultNotReadyException>(() =>
            QueryService(store).GetResultAsync(document.Id, TestContext.Current.CancellationToken));

        Assert.Equal(DocumentStatus.Queued, error.Status);
    }

    [Fact]
    public async Task Texto_de_documento_que_falhou_sem_resultado_anterior_e_conflito()
    {
        var (store, document) = StoreWithDocument(DocumentStatus.Failed);

        var error = await Assert.ThrowsAsync<ResultNotReadyException>(() =>
            QueryService(store).GetTextAsync(document.Id, TestContext.Current.CancellationToken));

        Assert.Equal(DocumentStatus.Failed, error.Status);
    }

    [Fact]
    public async Task Resultado_de_documento_inexistente_e_not_found_e_nao_conflito()
    {
        await Assert.ThrowsAsync<DocumentNotFoundException>(() =>
            QueryService(new InMemoryDocumentStore()).GetResultAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resultado_anterior_continua_disponivel_enquanto_o_reprocessamento_roda()
    {
        var (store, document) = StoreWithDocument(DocumentStatus.Completed);
        store.Extractions[document.Id] = (
            new ExtractionResultView(Summary, []),
            new ExtractionTextView(Summary, "[{\"pageNumber\":1,\"text\":\"x\"}]"));

        await ReprocessService(store).ReprocessAsync(document.Id, TestContext.Current.CancellationToken);

        var result = await QueryService(store).GetResultAsync(document.Id, TestContext.Current.CancellationToken);

        Assert.Equal(DocumentStatus.Queued, result.Document.Status);
        Assert.Equal(Summary.ExtractionId, result.Result.Summary.ExtractionId);
    }

    [Fact]
    public async Task Diagnostico_de_documento_sem_extracao_e_conflito_com_o_status_atual()
    {
        var (store, document) = StoreWithDocument(DocumentStatus.Queued);

        var error = await Assert.ThrowsAsync<ResultNotReadyException>(() =>
            QueryService(store).GetClassificationDiagnosticsAsync(document.Id, TestContext.Current.CancellationToken));

        Assert.Equal(DocumentStatus.Queued, error.Status);
    }

    [Fact]
    public async Task Diagnostico_de_documento_inexistente_e_not_found()
    {
        await Assert.ThrowsAsync<DocumentNotFoundException>(() =>
            QueryService(new InMemoryDocumentStore())
                .GetClassificationDiagnosticsAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Diagnostico_reclassifica_o_texto_da_ultima_extracao_com_as_regras_atuais()
    {
        var (store, document) = StoreWithDocument(DocumentStatus.Completed);
        store.Extractions[document.Id] = (
            new ExtractionResultView(Summary, []),
            new ExtractionTextView(
                Summary,
                "[{\"pageNumber\":1,\"text\":\"CADASTRO DE PESSOAS FISICAS\\nNASCIMENTO\"},{\"pageNumber\":2,\"text\":\"NUMERO DE INSCRICAO\"}]"));

        var diagnostics = await QueryService(store)
            .GetClassificationDiagnosticsAsync(document.Id, TestContext.Current.CancellationToken);

        Assert.Equal("BR_CPF_CARD", diagnostics.Diagnostics.DocumentType);
        Assert.Equal(2, diagnostics.Pages.Count);
        Assert.Equal("NUMERO DE INSCRICAO", diagnostics.Pages[1].Text);
        Assert.Equal(Summary.ClassifierVersion, diagnostics.Extraction.ClassifierVersion);
        Assert.Equal(RulesDocumentClassifier.ClassifierVersion, diagnostics.Diagnostics.ClassifierVersion);
    }

    [Fact]
    public async Task Snapshot_traz_o_job_mais_recente_e_o_resumo_da_extracao()
    {
        var (store, document) = StoreWithDocument(DocumentStatus.Queued);
        store.Extractions[document.Id] = (new ExtractionResultView(Summary, []), new ExtractionTextView(Summary, "[]"));

        var snapshot = await QueryService(store).GetSnapshotAsync(document.Id, includeEvents: false, TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot.LatestJob);
        Assert.Equal(Summary.ExtractionId, snapshot.LatestExtraction!.ExtractionId);
    }

    [Fact]
    public async Task Reprocessar_documento_concluido_enfileira_de_novo_com_job_novo()
    {
        var (store, document) = StoreWithDocument(DocumentStatus.Completed);

        var queued = await ReprocessService(store).ReprocessAsync(document.Id, TestContext.Current.CancellationToken);

        Assert.Equal(DocumentStatus.Queued, queued.Status);
        Assert.Null(queued.CompletedAt);
        var job = Assert.Single(store.Jobs);
        Assert.Equal(ProcessingJobStatus.Pending, job.Status);
        Assert.Contains(document.Events, e => e.EventType == DocumentEventTypes.Queued && e.Details == "REPROCESS_REQUESTED");
    }

    [Fact]
    public async Task Reprocessar_documento_falho_tambem_e_permitido()
    {
        var (store, document) = StoreWithDocument(DocumentStatus.Failed);

        var queued = await ReprocessService(store).ReprocessAsync(document.Id, TestContext.Current.CancellationToken);

        Assert.Equal(DocumentStatus.Queued, queued.Status);
        Assert.Null(queued.LastErrorCode);
    }

    [Fact]
    public async Task Reprocessar_documento_com_job_ativo_e_conflito_e_nao_cria_outro_job()
    {
        var (store, document) = StoreWithDocument(DocumentStatus.Queued);

        await Assert.ThrowsAsync<ReprocessConflictException>(() =>
            ReprocessService(store).ReprocessAsync(document.Id, TestContext.Current.CancellationToken));

        Assert.Single(store.Jobs);
    }

    [Fact]
    public async Task Reprocessar_documento_inexistente_e_not_found()
    {
        await Assert.ThrowsAsync<DocumentNotFoundException>(() =>
            ReprocessService(new InMemoryDocumentStore()).ReprocessAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }
}
