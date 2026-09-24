using DocReader.Application.Abstractions;
using DocReader.Application.Documents;
using DocReader.Application.Options;
using DocReader.Domain.Documents;
using DocReader.Domain.Extractions;
using DocReader.Domain.Processing;
using DocReader.Infrastructure.Persistence;
using DocReader.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// The writes of the worker and the reprocessing of RF-013, against a real PostgreSQL: the result is
/// persisted atomically with the completion of the job, a worker that lost its lock persists nothing,
/// and two reprocess requests for one document never produce two live jobs.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ProcessingPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static PostgresProcessingQueue QueueAt(DocReaderDbContext context, DateTimeOffset at, ProcessingQueueOptions? options = null) =>
        new(
            context,
            Options.Create(options ?? new ProcessingQueueOptions { WorkerName = "worker-test" }),
            new FakeTimeProvider(at),
            NullLogger<PostgresProcessingQueue>.Instance);

    private async Task<Guid> SeedDocumentAsync(DocumentStatus finalStatus = DocumentStatus.Queued)
    {
        await using var context = fixture.CreateContext();

        var id = Guid.CreateVersion7(Now);
        var document = Document.Accept(
            id,
            $"DOC-20260924-{Random.Shared.Next(1, 999_999):D6}",
            "cartao-cpf.png",
            $"documents/2026/09/24/{id:D}/original.png",
            "image/png",
            2048,
            new string('b', 64),
            1,
            UploadChannel.Api,
            null,
            null,
            Now);

        if (finalStatus != DocumentStatus.Stored)
        {
            document.MarkQueued(Now);
        }

        context.Documents.Add(document);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return id;
    }

    private static DocumentExtraction NewExtraction(Guid documentId, Guid jobId, string cpf = "11144477735") =>
        DocumentExtraction.Create(
            documentId,
            jobId,
            "paddleocr",
            "PP-OCRv5 test",
            "rules-1.0.0",
            "br-cpf-card-1.0.0",
            1,
            "REPUBLICA FEDERATIVA DO BRASIL\n111.444.777-35",
            "[{\"pageNumber\":1,\"text\":\"REPUBLICA FEDERATIVA DO BRASIL\\n111.444.777-35\"}]",
            "{\"pages\":[{\"page\":1,\"raw\":{}}]}",
            $"{{\"cpf\":\"{cpf}\"}}",
            0.9957m,
            Now,
            [
                ExtractedField.Create("cpf", "111.444.777-35", cpf, 1.0m, 1, "[110,332,470,332,470,380,110,380]", "VALID", "[\"CHECK_DIGIT_VALID\"]"),
                ExtractedField.Create("name", null, null, null, null, "[]", "NOT_FOUND", "[]")
            ]);

    private async Task<(ProcessingJob Job, Guid DocumentId)> AcquireJobAsync()
    {
        var documentId = await SeedDocumentAsync();

        await using var context = fixture.CreateContext();
        var queue = QueueAt(context, Now);

        await queue.EnqueueAsync(documentId, TestContext.Current.CancellationToken);
        var job = await queue.AcquireNextAsync(TestContext.Current.CancellationToken);

        return (job!, documentId);
    }

    private DocumentProcessingStore StoreFor(DocReaderDbContext context) => new(context, new FakeTimeProvider(Now.AddSeconds(30)));

    [Fact]
    public async Task Completar_grava_resultado_e_campos_marca_o_documento_e_fecha_o_job_de_uma_vez()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var (job, documentId) = await AcquireJobAsync();

        await using (var context = fixture.CreateContext())
        {
            var store = StoreFor(context);
            await store.AdvanceStageAsync(documentId, DocumentStatus.Preprocessing, DocumentEventTypes.PreprocessingStarted, null, TestContext.Current.CancellationToken);
            await store.AdvanceStageAsync(documentId, DocumentStatus.OcrRunning, DocumentEventTypes.OcrStarted, null, TestContext.Current.CancellationToken);
            await store.RecordProgressAsync(documentId, DocumentEventTypes.OcrPageCompleted, "page=1/1 durationMs=7000 blocks=12", TestContext.Current.CancellationToken);

            var completed = await store.CompleteAsync(
                job,
                NewExtraction(documentId, job.Id),
                "BR_CPF_CARD",
                1.0m,
                "type=BR_CPF_CARD confidence=1",
                TestContext.Current.CancellationToken);

            Assert.True(completed);
        }

        await using var verification = fixture.CreateContext();

        var document = await verification.Documents.AsNoTracking().Include(d => d.Events)
            .FirstAsync(d => d.Id == documentId, TestContext.Current.CancellationToken);
        Assert.Equal(DocumentStatus.Completed, document.Status);
        Assert.Equal("BR_CPF_CARD", document.DetectedDocumentType);
        Assert.Equal(1.0m, document.ClassificationConfidence);
        Assert.NotNull(document.CompletedAt);
        Assert.Null(document.LastErrorCode);
        Assert.Equal(
            [
                DocumentEventTypes.Received,
                DocumentEventTypes.Stored,
                DocumentEventTypes.Queued,
                DocumentEventTypes.PreprocessingStarted,
                DocumentEventTypes.OcrStarted,
                DocumentEventTypes.OcrPageCompleted,
                DocumentEventTypes.Classified,
                DocumentEventTypes.Completed
            ],
            document.Events.OrderBy(e => e.OccurredAt).ThenBy(e => e.Stage).Select(e => e.EventType).ToArray());

        var storedJob = await verification.ProcessingJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ProcessingJobStatus.Completed, storedJob.Status);

        var extraction = await verification.Extractions.AsNoTracking().Include(e => e.Fields)
            .SingleAsync(e => e.DocumentId == documentId, TestContext.Current.CancellationToken);
        Assert.Equal("paddleocr", extraction.OcrProvider);
        Assert.Equal("PP-OCRv5 test", extraction.OcrModelVersion);
        Assert.Equal(0.9957m, extraction.OverallConfidence);
        Assert.Equal("{\"cpf\": \"11144477735\"}", extraction.StructuredResultJson);
        Assert.Equal(2, extraction.Fields.Count);

        var cpf = extraction.Fields.Single(f => f.FieldPath == "cpf");
        Assert.Equal("111.444.777-35", cpf.RawValue);
        Assert.Equal("11144477735", cpf.NormalizedValue);
        Assert.Equal("VALID", cpf.ValidationStatus);
        Assert.Equal("[\"CHECK_DIGIT_VALID\"]", cpf.ValidationMessagesJson.Replace(" ", string.Empty));
    }

    [Fact]
    public async Task Worker_que_perdeu_a_reserva_nao_grava_resultado_nenhum()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var (job, documentId) = await AcquireJobAsync();

        // O job é recuperado como preso e assumido por outro worker antes de o primeiro terminar.
        await using (var other = fixture.CreateContext())
        {
            var options = new ProcessingQueueOptions { WorkerName = "worker-novo", JobLockTimeout = TimeSpan.FromMinutes(10) };
            var takenOver = await QueueAt(other, Now.AddMinutes(20), options).AcquireNextAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, takenOver!.AttemptCount);
        }

        await using (var context = fixture.CreateContext())
        {
            var completed = await StoreFor(context).CompleteAsync(
                job,
                NewExtraction(documentId, job.Id),
                "BR_CPF_CARD",
                1.0m,
                null,
                TestContext.Current.CancellationToken);

            Assert.False(completed);
        }

        await using var verification = fixture.CreateContext();
        Assert.Equal(0, await verification.Extractions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await verification.ExtractedFields.CountAsync(TestContext.Current.CancellationToken));

        var document = await verification.Documents.AsNoTracking().FirstAsync(d => d.Id == documentId, TestContext.Current.CancellationToken);
        Assert.NotEqual(DocumentStatus.Completed, document.Status);
        Assert.Null(document.DetectedDocumentType);

        var storedJob = await verification.ProcessingJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id, TestContext.Current.CancellationToken);
        Assert.Equal(ProcessingJobStatus.Running, storedJob.Status);
        Assert.Equal("worker-novo", storedJob.LockedBy);
    }

    [Fact]
    public async Task Completar_de_novo_o_mesmo_job_nao_duplica_a_extracao()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var (job, documentId) = await AcquireJobAsync();

        await using (var context = fixture.CreateContext())
        {
            Assert.True(await StoreFor(context).CompleteAsync(job, NewExtraction(documentId, job.Id), "BR_CPF_CARD", 1.0m, null, TestContext.Current.CancellationToken));
        }

        // Simula "gravou e caiu antes de confirmar": o job volta a RUNNING na mesma tentativa e é completado outra vez.
        await using (var context = fixture.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync("UPDATE processing_jobs SET status = 'RUNNING' WHERE status = 'COMPLETED';", TestContext.Current.CancellationToken);
            await context.Database.ExecuteSqlRawAsync("UPDATE documents SET status = 'OCR_RUNNING';", TestContext.Current.CancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            Assert.True(await StoreFor(context).CompleteAsync(job, NewExtraction(documentId, job.Id), "BR_CPF_CARD", 1.0m, null, TestContext.Current.CancellationToken));
        }

        await using var verification = fixture.CreateContext();
        Assert.Equal(1, await verification.Extractions.CountAsync(e => e.ProcessingJobId == job.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Excluir_o_documento_remove_extracoes_e_campos()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var (job, documentId) = await AcquireJobAsync();

        await using (var context = fixture.CreateContext())
        {
            await StoreFor(context).CompleteAsync(job, NewExtraction(documentId, job.Id), "BR_CPF_CARD", 1.0m, null, TestContext.Current.CancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            Assert.True(await new DocumentRepository(context).DeleteAsync(documentId, TestContext.Current.CancellationToken));
        }

        await using var verification = fixture.CreateContext();
        Assert.Equal(0, await verification.Extractions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await verification.ExtractedFields.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await verification.ProcessingJobs.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Consultas_de_resultado_devolvem_a_extracao_mais_recente_sem_carregar_o_bruto()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var (job, documentId) = await AcquireJobAsync();

        await using (var context = fixture.CreateContext())
        {
            await StoreFor(context).CompleteAsync(job, NewExtraction(documentId, job.Id), "BR_CPF_CARD", 1.0m, null, TestContext.Current.CancellationToken);
        }

        await using var reading = fixture.CreateContext();
        var repository = new DocumentRepository(reading);

        var result = await repository.FindLatestExtractionResultAsync(documentId, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(["cpf", "name"], result!.Fields.Select(field => field.FieldPath));
        Assert.Equal("br-cpf-card-1.0.0", result.Summary.ExtractorVersion);

        var text = await repository.FindLatestExtractionTextAsync(documentId, TestContext.Current.CancellationToken);
        Assert.NotNull(text);
        Assert.Contains("REPUBLICA FEDERATIVA DO BRASIL", text!.PageTextsJson);

        var latestJob = await repository.FindLatestJobAsync(documentId, TestContext.Current.CancellationToken);
        Assert.Equal(job.Id, latestJob!.Id);

        Assert.Null(await repository.FindLatestExtractionResultAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reprocessar_documento_concluido_cria_novo_job_e_preserva_o_resultado_anterior()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var (job, documentId) = await AcquireJobAsync();

        await using (var context = fixture.CreateContext())
        {
            await StoreFor(context).CompleteAsync(job, NewExtraction(documentId, job.Id), "BR_CPF_CARD", 1.0m, null, TestContext.Current.CancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            var outcome = await new DocumentRepository(context)
                .QueueReprocessingAsync(documentId, Now.AddHours(1), TestContext.Current.CancellationToken);

            Assert.Equal(ReprocessOutcome.Queued, outcome);
        }

        await using var verification = fixture.CreateContext();

        var document = await verification.Documents.AsNoTracking().Include(d => d.Events)
            .FirstAsync(d => d.Id == documentId, TestContext.Current.CancellationToken);
        Assert.Equal(DocumentStatus.Queued, document.Status);
        Assert.Null(document.CompletedAt);
        Assert.Contains(document.Events, e => e.EventType == DocumentEventTypes.Queued && e.Details == "REPROCESS_REQUESTED");

        var jobs = await verification.ProcessingJobs.AsNoTracking().Where(j => j.DocumentId == documentId).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, jobs.Count);
        Assert.Single(jobs, j => j.Status == ProcessingJobStatus.Pending);

        // O resultado anterior continua lá: reprocessar acrescenta, não substitui.
        Assert.Equal(1, await verification.Extractions.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reprocessar_documento_ainda_na_fila_ou_em_andamento_e_conflito()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var (_, documentId) = await AcquireJobAsync();

        await using var context = fixture.CreateContext();
        var repository = new DocumentRepository(context);

        Assert.Equal(ReprocessOutcome.Conflict, await repository.QueueReprocessingAsync(documentId, Now.AddMinutes(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reprocessar_documento_inexistente_e_not_found()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        await using var context = fixture.CreateContext();

        Assert.Equal(
            ReprocessOutcome.NotFound,
            await new DocumentRepository(context).QueueReprocessingAsync(Guid.NewGuid(), Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dois_pedidos_simultaneos_de_reprocessamento_geram_um_unico_job_vivo()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var (job, documentId) = await AcquireJobAsync();

        await using (var context = fixture.CreateContext())
        {
            await StoreFor(context).CompleteAsync(job, NewExtraction(documentId, job.Id), "BR_CPF_CARD", 1.0m, null, TestContext.Current.CancellationToken);
        }

        await using var contextA = fixture.CreateContext();
        await using var contextB = fixture.CreateContext();

        var outcomes = await Task.WhenAll(
            new DocumentRepository(contextA).QueueReprocessingAsync(documentId, Now.AddHours(1), TestContext.Current.CancellationToken),
            new DocumentRepository(contextB).QueueReprocessingAsync(documentId, Now.AddHours(1), TestContext.Current.CancellationToken));

        Assert.Single(outcomes, ReprocessOutcome.Queued);
        Assert.Single(outcomes, ReprocessOutcome.Conflict);

        await using var verification = fixture.CreateContext();
        Assert.Equal(
            1,
            await verification.ProcessingJobs.CountAsync(
                j => j.DocumentId == documentId && (j.Status == ProcessingJobStatus.Pending || j.Status == ProcessingJobStatus.Running),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task O_banco_recusa_dois_jobs_vivos_para_o_mesmo_documento_mesmo_sem_passar_pelo_servico()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var documentId = await SeedDocumentAsync();

        await using var context = fixture.CreateContext();
        context.ProcessingJobs.Add(ProcessingJob.CreateForDocument(documentId, Now));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var second = fixture.CreateContext();
        second.ProcessingJobs.Add(ProcessingJob.CreateForDocument(documentId, Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync(TestContext.Current.CancellationToken));
    }
}
