using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using DocReader.Domain.Documents;
using DocReader.Domain.Processing;
using DocReader.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// Prova, contra um PostgreSQL de verdade, o mecanismo da ADR 0001. O <c>FOR UPDATE SKIP LOCKED</c>
/// não tem como ser testado com banco em memória: o comportamento é do banco, não do EF.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PostgresProcessingQueueTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private PostgresProcessingQueue QueueFor(
        DocReader.Infrastructure.Persistence.DocReaderDbContext context,
        string workerName,
        ProcessingQueueOptions? options = null)
    {
        options ??= new ProcessingQueueOptions();
        options.WorkerName = workerName;

        return new PostgresProcessingQueue(
            context,
            Options.Create(options),
            new FakeTimeProvider(Now),
            NullLogger<PostgresProcessingQueue>.Instance);
    }

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
        var afterTimeout = new PostgresProcessingQueue(
            context,
            Options.Create(options),
            new FakeTimeProvider(Now.AddMinutes(30)),
            NullLogger<PostgresProcessingQueue>.Instance);

        var recovered = await afterTimeout.AcquireNextAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(recovered);
        Assert.Equal(reserved!.Id, recovered!.Id);
        Assert.Equal(2, recovered.AttemptCount);
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

        await queue.FailAsync(
            job!.Id,
            new ProcessingError("OCR_TIMEOUT", "serviço de OCR não respondeu", IsTransient: true),
            TestContext.Current.CancellationToken);

        await using var verification = fixture.CreateContext();
        var stored = await verification.ProcessingJobs.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == job.Id, TestContext.Current.CancellationToken);
        var document = await verification.Documents.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == documentId, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessingJobStatus.Pending, stored.Status);
        Assert.True(stored.AvailableAt > Now, "a retentativa precisa ficar no futuro");
        Assert.Equal(DocumentStatus.Queued, document.Status);
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

        await queue.FailAsync(
            job!.Id,
            new ProcessingError("OCR_UNREADABLE", "página ilegível", IsTransient: false),
            TestContext.Current.CancellationToken);

        await using var verification = fixture.CreateContext();
        var stored = await verification.ProcessingJobs.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == job.Id, TestContext.Current.CancellationToken);
        var document = await verification.Documents.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == documentId, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessingJobStatus.Failed, stored.Status);
        Assert.Equal(DocumentStatus.Failed, document.Status);
        Assert.Equal("OCR_UNREADABLE", document.LastErrorCode);
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

        await queue.CompleteAsync(job!.Id, TestContext.Current.CancellationToken);

        await using var verification = fixture.CreateContext();
        var stored = await verification.ProcessingJobs.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == job.Id, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessingJobStatus.Completed, stored.Status);
        Assert.Null(stored.LockedAt);
        Assert.Null(stored.LockedBy);
        Assert.Null(stored.ErrorCode);
        Assert.NotNull(stored.FinishedAt);
    }
}
