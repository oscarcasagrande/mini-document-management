using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using DocReader.Domain.Documents;
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
/// Prova, contra um PostgreSQL de verdade, o mecanismo da ADR 0001 e o heartbeat da ADR 0002. O
/// <c>FOR UPDATE SKIP LOCKED</c> não tem como ser testado com banco em memória: o comportamento é do
/// banco, não do EF.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PostgresProcessingQueueTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static PostgresProcessingQueue QueueAt(
        DocReaderDbContext context,
        string workerName,
        DateTimeOffset at,
        ProcessingQueueOptions? options = null)
    {
        options ??= new ProcessingQueueOptions();
        options.WorkerName = workerName;

        return new PostgresProcessingQueue(
            context,
            Options.Create(options),
            new FakeTimeProvider(at),
            NullLogger<PostgresProcessingQueue>.Instance);
    }

    private PostgresProcessingQueue QueueFor(DocReaderDbContext context, string workerName, ProcessingQueueOptions? options = null) =>
        QueueAt(context, workerName, Now, options);

    private async Task<Guid> SeedDocumentAsync(DateTimeOffset uploadedAt)
    {
        await using var context = fixture.CreateContext();

        var id = Guid.CreateVersion7(uploadedAt);
        var document = Document.Accept(
            id,
            $"DOC-20260924-{Random.Shared.Next(1, 999_999):D6}",
            "amostra.pdf",
            $"documents/2026/09/24/{id:D}/original.pdf",
            "application/pdf",
            1234,
            new string('a', 64),
            1,
            UploadChannel.Api,
            null,
            null,
            uploadedAt);

        document.MarkQueued(uploadedAt);
        context.Documents.Add(document);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return id;
    }

    private async Task<ProcessingJob> ReadJobAsync(Guid jobId)
    {
        await using var context = fixture.CreateContext();
        return await context.ProcessingJobs.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == jobId, TestContext.Current.CancellationToken);
    }

    private async Task<Document> ReadDocumentAsync(Guid documentId)
    {
        await using var context = fixture.CreateContext();
        return await context.Documents.AsNoTracking()
            .Include(candidate => candidate.Events)
            .FirstAsync(candidate => candidate.Id == documentId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Dois_workers_concorrentes_nunca_pegam_o_mesmo_job()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var first = await SeedDocumentAsync(Now);
        var second = await SeedDocumentAsync(Now.AddSeconds(1));

        await using (var context = fixture.CreateContext())
        {
            await QueueFor(context, "seed").EnqueueAsync(first, TestContext.Current.CancellationToken);
            await QueueFor(context, "seed").EnqueueAsync(second, TestContext.Current.CancellationToken);
        }

        // Cada worker tem a própria conexão, que é o que torna o teste representativo.
        await using var contextA = fixture.CreateContext();
        await using var contextB = fixture.CreateContext();

        var acquired = await Task.WhenAll(
            QueueFor(contextA, "worker-a").AcquireNextAsync(TestContext.Current.CancellationToken),
            QueueFor(contextB, "worker-b").AcquireNextAsync(TestContext.Current.CancellationToken));

        Assert.All(acquired, job => Assert.NotNull(job));
        Assert.NotEqual(acquired[0]!.DocumentId, acquired[1]!.DocumentId);
        Assert.Equal(1, acquired[0]!.AttemptCount);
    }

    [Fact]
    public async Task Fila_vazia_devolve_null_sem_bloquear()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        await using var context = fixture.CreateContext();

        var job = await QueueFor(context, "worker-vazio").AcquireNextAsync(TestContext.Current.CancellationToken);

        Assert.Null(job);
    }

    [Fact]
    public async Task Job_preso_volta_para_a_fila_depois_do_timeout()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var documentId = await SeedDocumentAsync(Now);

        await using var context = fixture.CreateContext();
        var options = new ProcessingQueueOptions { JobLockTimeout = TimeSpan.FromMinutes(10) };

        await QueueFor(context, "worker-que-morre", options).EnqueueAsync(documentId, TestContext.Current.CancellationToken);

        var reserved = await QueueFor(context, "worker-que-morre", options)
            .AcquireNextAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(reserved);

        // Ninguém completou nem falhou o job: o worker "morreu" com a linha em RUNNING.
        var recovered = await QueueAt(context, "worker-novo", Now.AddMinutes(30), options)
            .AcquireNextAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(recovered);
        Assert.Equal(reserved!.Id, recovered!.Id);
        Assert.Equal(2, recovered.AttemptCount);

        // O documento não fica mostrando "lendo" enquanto ninguém lê: a recuperação o devolve à fila.
        var document = await ReadDocumentAsync(documentId);
        Assert.Equal(DocumentStatus.Queued, document.Status);
        Assert.Contains(document.Events, entry =>
            entry.EventType == DocumentEventTypes.RetryScheduled && entry.Details == "STUCK_JOB_RECOVERED");
    }

    [Fact]
    public async Task Job_longo_que_renova_a_reserva_a_cada_pagina_nao_e_pego_por_um_segundo_worker()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        // Tempo simulado: cada worker enxerga o relógio do instante em que age.
        var options = new ProcessingQueueOptions { JobLockTimeout = TimeSpan.FromMinutes(10) };
        var start = Now;
        var documentId = await SeedDocumentAsync(start);

        await using var contextA = fixture.CreateContext();
        await using var contextB = fixture.CreateContext();

        await QueueAt(contextA, "worker-a", start, options).EnqueueAsync(documentId, TestContext.Current.CancellationToken);
        var job = await QueueAt(contextA, "worker-a", start, options).AcquireNextAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(job);

        // Um documento de 8 páginas a 4 minutos por página: 32 minutos no total, mais de três vezes o
        // timeout de reserva. Nenhuma página passa perto do timeout sozinha.
        const int pages = 8;
        for (var page = 1; page <= pages; page++)
        {
            var at = start.AddMinutes(4 * page);

            var renewed = await QueueAt(contextA, "worker-a", at, options)
                .HeartbeatAsync(job!, page, pages, TestContext.Current.CancellationToken);
            Assert.True(renewed, $"o heartbeat da página {page} deveria ser aceito");

            // Um segundo worker faz a sondagem logo depois: não pode achar o job preso.
            var stolen = await QueueAt(contextB, "worker-b", at.AddSeconds(30), options)
                .AcquireNextAsync(TestContext.Current.CancellationToken);
            Assert.Null(stolen);

            var running = await ReadJobAsync(job!.Id);
            Assert.Equal(ProcessingJobStatus.Running, running.Status);
            Assert.Equal("worker-a", running.LockedBy);
            Assert.Equal(1, running.AttemptCount);
            Assert.Equal(page, running.PagesCompleted);
            Assert.Equal(pages, running.PageCount);
        }

        // O worker termina o trabalho, ainda dono do job, mais de 30 minutos depois de começar.
        await QueueAt(contextA, "worker-a", start.AddMinutes(33), options)
            .CompleteAsync(job!, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessingJobStatus.Completed, (await ReadJobAsync(job!.Id)).Status);
    }

    [Fact]
    public async Task Sem_heartbeat_o_job_e_recuperado_e_o_worker_antigo_perde_todos_os_direitos_sobre_ele()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var options = new ProcessingQueueOptions { JobLockTimeout = TimeSpan.FromMinutes(10) };
        var documentId = await SeedDocumentAsync(Now);

        await using var contextA = fixture.CreateContext();
        await using var contextB = fixture.CreateContext();

        await QueueAt(contextA, "worker-a", Now, options).EnqueueAsync(documentId, TestContext.Current.CancellationToken);
        var original = await QueueAt(contextA, "worker-a", Now, options).AcquireNextAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(original);

        // O worker A dá um heartbeat e some (congelou, perdeu a rede...).
        Assert.True(await QueueAt(contextA, "worker-a", Now.AddMinutes(1), options)
            .HeartbeatAsync(original!, 1, 5, TestContext.Current.CancellationToken));

        // Onze minutos de silêncio depois, o worker B assume.
        var takenOver = await QueueAt(contextB, "worker-b", Now.AddMinutes(12), options)
            .AcquireNextAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(takenOver);
        Assert.Equal(original!.Id, takenOver!.Id);
        Assert.Equal(2, takenOver.AttemptCount);
        Assert.Equal(0, takenOver.PagesCompleted);

        // O worker A acorda e tenta agir. Nada do que ele faz pode valer.
        var queueA = QueueAt(contextA, "worker-a", Now.AddMinutes(13), options);

        Assert.False(await queueA.HeartbeatAsync(original, 2, 5, TestContext.Current.CancellationToken));

        await queueA.CompleteAsync(original, TestContext.Current.CancellationToken);
        var failure = await queueA.FailAsync(
            original,
            new ProcessingError("OCR_UNAVAILABLE", "tarde demais", IsTransient: false),
            TestContext.Current.CancellationToken);
        Assert.False(failure.Applied);

        await queueA.ReleaseAsync(original, TestContext.Current.CancellationToken);

        var stored = await ReadJobAsync(original.Id);
        Assert.Equal(ProcessingJobStatus.Running, stored.Status);
        Assert.Equal("worker-b", stored.LockedBy);
        Assert.Equal(2, stored.AttemptCount);

        // E o B, que é o dono, conclui normalmente.
        await QueueAt(contextB, "worker-b", Now.AddMinutes(14), options)
            .CompleteAsync(takenOver, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessingJobStatus.Completed, (await ReadJobAsync(original.Id)).Status);
    }

    [Fact]
    public async Task Heartbeat_de_job_que_nao_esta_rodando_e_recusado()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var documentId = await SeedDocumentAsync(Now);

        await using var context = fixture.CreateContext();
        var queue = QueueFor(context, "worker-ok");

        await queue.EnqueueAsync(documentId, TestContext.Current.CancellationToken);
        var job = await queue.AcquireNextAsync(TestContext.Current.CancellationToken);
        await queue.CompleteAsync(job!, TestContext.Current.CancellationToken);

        Assert.False(await queue.HeartbeatAsync(job!, 1, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Liberar_devolve_o_job_sem_gastar_tentativa_e_recoloca_o_documento_na_fila()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var documentId = await SeedDocumentAsync(Now);

        await using var context = fixture.CreateContext();
        var queue = QueueFor(context, "worker-que-desliga");

        await queue.EnqueueAsync(documentId, TestContext.Current.CancellationToken);
        var job = await queue.AcquireNextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, job!.AttemptCount);

        await queue.ReleaseAsync(job, TestContext.Current.CancellationToken);

        var stored = await ReadJobAsync(job.Id);
        Assert.Equal(ProcessingJobStatus.Pending, stored.Status);
        Assert.Equal(0, stored.AttemptCount);
        Assert.Null(stored.LockedBy);

        Assert.Equal(DocumentStatus.Queued, (await ReadDocumentAsync(documentId)).Status);

        // Está de volta na fila, e a próxima tentativa é a primeira de novo.
        var again = await QueueFor(context, "worker-que-sobe").AcquireNextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(job.Id, again!.Id);
        Assert.Equal(1, again.AttemptCount);
    }

    [Fact]
    public async Task Falha_transitoria_reagenda_em_vez_de_derrubar_o_documento()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var documentId = await SeedDocumentAsync(Now);

        await using var context = fixture.CreateContext();
        var queue = QueueFor(context, "worker-retry");

        await queue.EnqueueAsync(documentId, TestContext.Current.CancellationToken);
        var job = await queue.AcquireNextAsync(TestContext.Current.CancellationToken);

        // O documento estava sendo lido quando o serviço de OCR caiu.
        await using (var progress = fixture.CreateContext())
        {
            var reading = await progress.Documents.FirstAsync(d => d.Id == documentId, TestContext.Current.CancellationToken);
            reading.AdvanceTo(DocumentStatus.OcrRunning, DocumentEventTypes.OcrStarted, Now);
            await progress.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var outcome = await queue.FailAsync(
            job!,
            new ProcessingError("OCR_UNAVAILABLE", "serviço de OCR não respondeu", IsTransient: true),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Applied);
        Assert.True(outcome.WillRetry);
        Assert.True(outcome.AvailableAt > Now);

        var stored = await ReadJobAsync(job!.Id);
        var document = await ReadDocumentAsync(documentId);

        Assert.Equal(ProcessingJobStatus.Pending, stored.Status);
        Assert.True(stored.AvailableAt > Now, "a retentativa precisa ficar no futuro");
        Assert.Equal(DocumentStatus.Queued, document.Status);
        Assert.Equal("OCR_UNAVAILABLE", document.LastErrorCode);
        Assert.Contains(document.Events, entry => entry.EventType == DocumentEventTypes.RetryScheduled);
    }

    [Fact]
    public async Task Job_com_retry_agendado_volta_a_ser_processado_depois_do_atraso_e_o_erro_some_ao_completar()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var documentId = await SeedDocumentAsync(Now);

        await using var context = fixture.CreateContext();
        var queue = QueueFor(context, "worker-retry");

        await queue.EnqueueAsync(documentId, TestContext.Current.CancellationToken);
        var first = await queue.AcquireNextAsync(TestContext.Current.CancellationToken);

        var outcome = await queue.FailAsync(
            first!,
            new ProcessingError("OCR_UNAVAILABLE", "fora do ar", IsTransient: true),
            TestContext.Current.CancellationToken);

        // Antes do atraso o job não é elegível; depois dele, é a segunda tentativa do mesmo job.
        Assert.Null(await QueueAt(context, "worker-retry", Now.AddSeconds(1)).AcquireNextAsync(TestContext.Current.CancellationToken));

        var second = await QueueAt(context, "worker-retry", outcome.AvailableAt!.Value.AddSeconds(1))
            .AcquireNextAsync(TestContext.Current.CancellationToken);

        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(2, second.AttemptCount);
    }

    [Fact]
    public async Task Tentativas_esgotadas_marcam_documento_como_failed()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var documentId = await SeedDocumentAsync(Now);

        await using var context = fixture.CreateContext();
        var options = new ProcessingQueueOptions { MaxAttempts = 1 };
        var queue = QueueFor(context, "worker-falha", options);

        await queue.EnqueueAsync(documentId, TestContext.Current.CancellationToken);
        var job = await queue.AcquireNextAsync(TestContext.Current.CancellationToken);

        var outcome = await queue.FailAsync(
            job!,
            new ProcessingError("OCR_UNREADABLE", "página ilegível", IsTransient: false),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Applied);
        Assert.False(outcome.WillRetry);

        var stored = await ReadJobAsync(job!.Id);
        var document = await ReadDocumentAsync(documentId);

        Assert.Equal(ProcessingJobStatus.Failed, stored.Status);
        Assert.Equal(DocumentStatus.Failed, document.Status);
        Assert.Equal("OCR_UNREADABLE", document.LastErrorCode);
        Assert.Contains(document.Events, entry => entry.EventType == DocumentEventTypes.Failed);
    }

    [Fact]
    public async Task Completar_limpa_a_reserva_e_o_erro()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var documentId = await SeedDocumentAsync(Now);

        await using var context = fixture.CreateContext();
        var queue = QueueFor(context, "worker-ok");

        await queue.EnqueueAsync(documentId, TestContext.Current.CancellationToken);
        var job = await queue.AcquireNextAsync(TestContext.Current.CancellationToken);

        await queue.CompleteAsync(job!, TestContext.Current.CancellationToken);

        var stored = await ReadJobAsync(job!.Id);

        Assert.Equal(ProcessingJobStatus.Completed, stored.Status);
        Assert.Null(stored.LockedAt);
        Assert.Null(stored.LockedBy);
        Assert.Null(stored.ErrorCode);
        Assert.NotNull(stored.FinishedAt);
    }
}
