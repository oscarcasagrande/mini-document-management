using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using DocReader.Application.Processing;
using DocReader.Domain.Documents;
using DocReader.Domain.Processing;
using DocReader.Infrastructure.Persistence;
using DocReader.Infrastructure.Queue;
using DocReader.Infrastructure.Queue.RabbitMq;
using DocReader.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// Proves the ADR 0004 mechanism against a real broker and a real PostgreSQL: publish -> acquire ->
/// complete, the duplicate-delivery idempotency guard, the per-attempt DLX retry, and the transactional
/// outbox surviving a broker that is down at upload time and comes back later.
/// </summary>
[Collection(nameof(RabbitMqCollection))]
public sealed class RabbitMqQueueIntegrationTests(RabbitMqFixture rabbitFixture, PostgresFixture postgresFixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await postgresFixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string UniqueQueueName() => $"docreader.test.{Guid.NewGuid():N}.jobs";

    private RabbitMqOptions RabbitOptionsFor(string queueName, TimeSpan? outboxPollInterval = null) => new()
    {
        HostName = rabbitFixture.HostName,
        Port = rabbitFixture.Port,
        UserName = rabbitFixture.UserName,
        Password = rabbitFixture.Password,
        QueueName = queueName,
        PrefetchCount = 1,
        OutboxPollInterval = outboxPollInterval ?? TimeSpan.FromMilliseconds(200),
        OutboxBatchSize = 10
    };

    private static async Task<RabbitMqJobBridge> StartBridgeAsync(
        RabbitMqOptions rabbitOptions,
        ProcessingQueueOptions queueOptions,
        CancellationToken ct)
    {
        var bridge = new RabbitMqJobBridge(
            Options.Create(rabbitOptions),
            Options.Create(queueOptions),
            NullLogger<RabbitMqJobBridge>.Instance);

        await bridge.StartAsync(ct);
        return bridge;
    }

    private static RabbitMqProcessingQueue QueueAt(
        DocReaderDbContext context,
        RabbitMqJobBridge bridge,
        string workerName,
        DateTimeOffset at,
        ProcessingQueueOptions? options = null)
    {
        options ??= new ProcessingQueueOptions();
        options.WorkerName = workerName;

        return new RabbitMqProcessingQueue(
            context,
            bridge,
            Options.Create(options),
            new FakeTimeProvider(at),
            NullLogger<RabbitMqProcessingQueue>.Instance);
    }

    /// <summary>Bounds <c>AcquireNextAsync</c>'s otherwise-indefinite wait for a delivery: null on timeout.</summary>
    private static async Task<ProcessingJob?> AcquireWithTimeoutAsync(
        DocReader.Application.Abstractions.IProcessingQueue queue,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            return await queue.AcquireNextAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<(Guid DocumentId, Guid JobId)> SeedDocumentAndJobAsync(DateTimeOffset uploadedAt)
    {
        await using var context = postgresFixture.CreateContext();

        var documentId = Guid.CreateVersion7(uploadedAt);
        var document = Document.Accept(
            documentId,
            $"DOC-RMQ-{Random.Shared.Next(1, 999_999):D6}",
            "amostra.pdf",
            $"documents/2026/09/24/{documentId:D}/original.pdf",
            "application/pdf",
            1234,
            new string('a', 64),
            1,
            UploadChannel.Api,
            null,
            null,
            uploadedAt);
        document.MarkQueued(uploadedAt);

        var job = ProcessingJob.CreateForDocument(documentId, uploadedAt);

        context.Documents.Add(document);
        context.ProcessingJobs.Add(job);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (documentId, job.Id);
    }

    private async Task<ProcessingJob> ReadJobAsync(Guid jobId)
    {
        await using var context = postgresFixture.CreateContext();
        return await context.ProcessingJobs.AsNoTracking()
            .FirstAsync(candidate => candidate.Id == jobId, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// In production, the PostgreSQL write of a successful attempt is
    /// <c>IDocumentProcessingStore.CompleteAsync</c> (called from inside <c>DocumentProcessor.RunAsync</c>,
    /// Application layer), never <c>IProcessingQueue.CompleteAsync</c> - that is the whole point of ADR
    /// 0004's ack-timing design: <c>RabbitMqProcessingQueue.CompleteAsync</c> only acks the broker
    /// delivery and deliberately does not touch <c>processing_jobs</c> itself. This mirrors just the
    /// job-row half of that store's write (the part these tests care about), so calling
    /// <c>queue.CompleteAsync</c> afterward exercises the real "already-completed-in-Postgres, now settle
    /// the broker side" sequence instead of asserting a status the queue was never meant to write.
    /// </summary>
    private async Task MarkCompletedInPostgresAsync(ProcessingJob job, CancellationToken ct)
    {
        await using var context = postgresFixture.CreateContext();

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE processing_jobs
             SET status = 'COMPLETED', finished_at = {Now}, locked_at = NULL, locked_by = NULL,
                 error_code = NULL, error_message = NULL
             WHERE id = {job.Id} AND status = 'RUNNING' AND attempt_count = {job.AttemptCount};
             """,
            ct);
    }

    [Fact]
    public async Task Publica_no_broker_real_e_o_worker_adquire_processa_e_completa()
    {
        Assert.SkipUnless(rabbitFixture.Available, rabbitFixture.SkipReason ?? "sem RabbitMQ");
        Assert.SkipUnless(postgresFixture.ConnectionString is not null, postgresFixture.SkipReason ?? "sem banco");

        var ct = TestContext.Current.CancellationToken;
        var (documentId, jobId) = await SeedDocumentAndJobAsync(Now);

        var queueOptions = new ProcessingQueueOptions { MaxAttempts = 3 };
        var rabbitOptions = RabbitOptionsFor(UniqueQueueName());

        await using var bridge = await StartBridgeAsync(rabbitOptions, queueOptions, ct);
        await bridge.PublishNewJobAsync(jobId, documentId, ct);

        await using var context = postgresFixture.CreateContext();
        var queue = QueueAt(context, bridge, "worker-rmq-a", Now, queueOptions);

        var job = await AcquireWithTimeoutAsync(queue, TimeSpan.FromSeconds(10), ct);

        Assert.NotNull(job);
        Assert.Equal(jobId, job!.Id);
        Assert.Equal(1, job.AttemptCount);

        await MarkCompletedInPostgresAsync(job, ct);
        await queue.CompleteAsync(job, ct);

        var stored = await ReadJobAsync(jobId);
        Assert.Equal(ProcessingJobStatus.Completed, stored.Status);

        await bridge.StopAsync(ct);
    }

    [Fact]
    public async Task Mensagem_repetida_nao_cria_segunda_execucao()
    {
        Assert.SkipUnless(rabbitFixture.Available, rabbitFixture.SkipReason ?? "sem RabbitMQ");
        Assert.SkipUnless(postgresFixture.ConnectionString is not null, postgresFixture.SkipReason ?? "sem banco");

        var ct = TestContext.Current.CancellationToken;
        var (documentId, jobId) = await SeedDocumentAndJobAsync(Now);

        var queueOptions = new ProcessingQueueOptions { MaxAttempts = 3 };
        var rabbitOptions = RabbitOptionsFor(UniqueQueueName());

        await using var bridge = await StartBridgeAsync(rabbitOptions, queueOptions, ct);

        // Simulates redelivery after a crash before the earlier ack landed: the exact same job
        // announcement, published twice. With prefetch=1 the broker only ever hands out one unacked
        // delivery at a time, so the duplicate is not even delivered until the first is acked.
        await bridge.PublishNewJobAsync(jobId, documentId, ct);
        await bridge.PublishNewJobAsync(jobId, documentId, ct);

        await using var context = postgresFixture.CreateContext();
        var queue = QueueAt(context, bridge, "worker-rmq-dup", Now, queueOptions);

        var first = await AcquireWithTimeoutAsync(queue, TimeSpan.FromSeconds(10), ct);
        Assert.NotNull(first);
        Assert.Equal(1, first!.AttemptCount);

        await MarkCompletedInPostgresAsync(first, ct);
        await queue.CompleteAsync(first, ct);

        // The duplicate is now delivered (status is COMPLETED, so the targeted UPDATE affects zero
        // rows): it must be acked without ever becoming a second execution, and nothing else follows.
        var second = await AcquireWithTimeoutAsync(queue, TimeSpan.FromSeconds(5), ct);
        Assert.Null(second);

        var stored = await ReadJobAsync(jobId);
        Assert.Equal(ProcessingJobStatus.Completed, stored.Status);
        Assert.Equal(1, stored.AttemptCount);

        await bridge.StopAsync(ct);
    }

    [Fact]
    public async Task Falha_transitoria_reentrega_pela_fila_de_atraso_como_segunda_tentativa()
    {
        Assert.SkipUnless(rabbitFixture.Available, rabbitFixture.SkipReason ?? "sem RabbitMQ");
        Assert.SkipUnless(postgresFixture.ConnectionString is not null, postgresFixture.SkipReason ?? "sem banco");

        var ct = TestContext.Current.CancellationToken;
        var (documentId, jobId) = await SeedDocumentAndJobAsync(Now);

        // Short delay so the test does not sleep for real minutes, per the task's own guidance.
        var queueOptions = new ProcessingQueueOptions
        {
            MaxAttempts = 3,
            RetryBaseDelay = TimeSpan.FromMilliseconds(300),
            RetryMaxDelay = TimeSpan.FromSeconds(2)
        };
        var rabbitOptions = RabbitOptionsFor(UniqueQueueName());

        await using var bridge = await StartBridgeAsync(rabbitOptions, queueOptions, ct);
        await bridge.PublishNewJobAsync(jobId, documentId, ct);

        await using var context = postgresFixture.CreateContext();
        var queue = QueueAt(context, bridge, "worker-rmq-retry", Now, queueOptions);

        var first = await AcquireWithTimeoutAsync(queue, TimeSpan.FromSeconds(10), ct);
        Assert.NotNull(first);
        Assert.Equal(1, first!.AttemptCount);

        var outcome = await queue.FailAsync(
            first,
            new ProcessingError("OCR_UNAVAILABLE", "serviço de OCR não respondeu", IsTransient: true),
            ct);

        Assert.True(outcome.Applied);
        Assert.True(outcome.WillRetry);

        // Dead-lettered back from docreader.test.....jobs.retry.1 after ~300ms: give it real headroom.
        var second = await AcquireWithTimeoutAsync(queue, TimeSpan.FromSeconds(10), ct);

        Assert.NotNull(second);
        Assert.Equal(jobId, second!.Id);
        Assert.Equal(2, second.AttemptCount);

        await MarkCompletedInPostgresAsync(second, ct);
        await queue.CompleteAsync(second, ct);
        Assert.Equal(ProcessingJobStatus.Completed, (await ReadJobAsync(jobId)).Status);

        await bridge.StopAsync(ct);
    }

    [Fact]
    public async Task Tentativas_esgotadas_nao_reentregam_e_marcam_o_job_como_failed()
    {
        Assert.SkipUnless(rabbitFixture.Available, rabbitFixture.SkipReason ?? "sem RabbitMQ");
        Assert.SkipUnless(postgresFixture.ConnectionString is not null, postgresFixture.SkipReason ?? "sem banco");

        var ct = TestContext.Current.CancellationToken;
        var (documentId, jobId) = await SeedDocumentAndJobAsync(Now);

        var queueOptions = new ProcessingQueueOptions { MaxAttempts = 1 };
        var rabbitOptions = RabbitOptionsFor(UniqueQueueName());

        await using var bridge = await StartBridgeAsync(rabbitOptions, queueOptions, ct);
        await bridge.PublishNewJobAsync(jobId, documentId, ct);

        await using var context = postgresFixture.CreateContext();
        var queue = QueueAt(context, bridge, "worker-rmq-final", Now, queueOptions);

        var job = await AcquireWithTimeoutAsync(queue, TimeSpan.FromSeconds(10), ct);
        Assert.NotNull(job);

        var outcome = await queue.FailAsync(
            job!,
            new ProcessingError("OCR_UNREADABLE", "página ilegível", IsTransient: false),
            ct);

        Assert.True(outcome.Applied);
        Assert.False(outcome.WillRetry);

        // MaxAttempts=1 means RabbitMqTopology declared zero delay queues: nothing to dead-letter from.
        var redelivered = await AcquireWithTimeoutAsync(queue, TimeSpan.FromSeconds(3), ct);
        Assert.Null(redelivered);

        Assert.Equal(ProcessingJobStatus.Failed, (await ReadJobAsync(jobId)).Status);

        await bridge.StopAsync(ct);
    }

    [Fact]
    public async Task Heartbeat_depois_de_completar_e_recusado_igual_ao_provedor_postgres()
    {
        Assert.SkipUnless(rabbitFixture.Available, rabbitFixture.SkipReason ?? "sem RabbitMQ");
        Assert.SkipUnless(postgresFixture.ConnectionString is not null, postgresFixture.SkipReason ?? "sem banco");

        var ct = TestContext.Current.CancellationToken;
        var (documentId, jobId) = await SeedDocumentAndJobAsync(Now);

        var queueOptions = new ProcessingQueueOptions { MaxAttempts = 3 };
        var rabbitOptions = RabbitOptionsFor(UniqueQueueName());

        await using var bridge = await StartBridgeAsync(rabbitOptions, queueOptions, ct);
        await bridge.PublishNewJobAsync(jobId, documentId, ct);

        await using var context = postgresFixture.CreateContext();
        var queue = QueueAt(context, bridge, "worker-rmq-heartbeat", Now, queueOptions);

        var job = await AcquireWithTimeoutAsync(queue, TimeSpan.FromSeconds(10), ct);
        Assert.NotNull(job);

        await MarkCompletedInPostgresAsync(job!, ct);
        await queue.CompleteAsync(job!, ct);

        Assert.False(await queue.HeartbeatAsync(job!, 1, 1, ct));

        await bridge.StopAsync(ct);
    }

    [Fact]
    public async Task Documento_aceito_com_broker_fora_do_ar_fica_na_fila_e_e_publicado_quando_o_broker_volta()
    {
        Assert.SkipUnless(rabbitFixture.Available, rabbitFixture.SkipReason ?? "sem RabbitMQ");
        Assert.SkipUnless(postgresFixture.ConnectionString is not null, postgresFixture.SkipReason ?? "sem banco");

        var ct = TestContext.Current.CancellationToken;
        var queueOptions = new ProcessingQueueOptions { MaxAttempts = 3 };
        var rabbitOptions = RabbitOptionsFor(UniqueQueueName());
        var rabbitMqProvider = new QueueProviderOptions(QueueProvider.RabbitMq);

        // 1. Real production path: DocumentRepository.AcceptAsync, in RabbitMQ mode, with no bridge
        //    involved at all. The API-level 202 does not depend on the broker in any way; only the
        //    outbox row does the announcing.
        Guid documentId;
        Guid jobId;

        await using (var context = postgresFixture.CreateContext())
        {
            var repository = new DocumentRepository(context, rabbitMqProvider);

            documentId = Guid.CreateVersion7(Now);
            var document = Document.Accept(
                documentId,
                $"DOC-RMQ-OUT-{Random.Shared.Next(1, 999_999):D6}",
                "amostra.pdf",
                $"documents/2026/09/24/{documentId:D}/original.pdf",
                "application/pdf",
                1234,
                new string('a', 64),
                1,
                UploadChannel.Api,
                null,
                null,
                Now);

            var job = ProcessingJob.CreateForDocument(documentId, Now);
            jobId = job.Id;

            await repository.AcceptAsync(document, job, null, ct);
        }

        await using (var verify = postgresFixture.CreateContext())
        {
            var outboxRow = await verify.OutboxMessages.AsNoTracking()
                .SingleAsync(candidate => candidate.AggregateId == documentId, ct);
            Assert.Null(outboxRow.PublishedAt);
        }

        var scopeFactory = BuildScopeFactory();

        // 2. "Broker down": a bridge that was never started. Its PublishNewJobAsync throws
        //    deterministically instead of a flaky socket timeout, but the effect on the publisher loop
        //    (log, increment attempts, leave published_at null, try again next poll) is the same one a
        //    genuinely unreachable broker would cause.
        var downBridge = new RabbitMqJobBridge(Options.Create(rabbitOptions), Options.Create(queueOptions), NullLogger<RabbitMqJobBridge>.Instance);
        var downPublisher = new RabbitMqOutboxPublisher(
            scopeFactory, downBridge, Options.Create(rabbitOptions), TimeProvider.System, NullLogger<RabbitMqOutboxPublisher>.Instance);

        await downPublisher.StartAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        await downPublisher.StopAsync(ct);

        await using (var verify = postgresFixture.CreateContext())
        {
            var stillUnpublished = await verify.OutboxMessages.AsNoTracking()
                .SingleAsync(candidate => candidate.AggregateId == documentId, ct);
            Assert.Null(stillUnpublished.PublishedAt);
        }

        // 3. Broker "comes back up": a real, started bridge and a publisher pointed at it.
        await using var liveBridge = await StartBridgeAsync(rabbitOptions, queueOptions, ct);
        var livePublisher = new RabbitMqOutboxPublisher(
            scopeFactory, liveBridge, Options.Create(rabbitOptions), TimeProvider.System, NullLogger<RabbitMqOutboxPublisher>.Instance);

        await livePublisher.StartAsync(ct);

        // 4. It actually flows through the broker: acquire it exactly like a worker would.
        await using var context2 = postgresFixture.CreateContext();
        var queue = QueueAt(context2, liveBridge, "worker-rmq-outbox", Now, queueOptions);

        var acquired = await AcquireWithTimeoutAsync(queue, TimeSpan.FromSeconds(10), ct);
        Assert.NotNull(acquired);
        Assert.Equal(jobId, acquired!.Id);

        await MarkCompletedInPostgresAsync(acquired, ct);
        await queue.CompleteAsync(acquired, ct);
        await livePublisher.StopAsync(ct);
        await liveBridge.StopAsync(ct);

        await using (var verify = postgresFixture.CreateContext())
        {
            var published = await verify.OutboxMessages.AsNoTracking()
                .SingleAsync(candidate => candidate.AggregateId == documentId, ct);
            Assert.NotNull(published.PublishedAt);

            var finalJob = await verify.ProcessingJobs.AsNoTracking().SingleAsync(candidate => candidate.Id == jobId, ct);
            Assert.Equal(ProcessingJobStatus.Completed, finalJob.Status);
        }
    }

    private IServiceScopeFactory BuildScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton(PostgresFixture.FieldEncryptionProtector);
        services.AddDbContext<DocReaderDbContext>(options => options.UseNpgsql(
            postgresFixture.ConnectionString ?? throw new InvalidOperationException("Sem banco de teste."),
            npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(1), null)));

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
