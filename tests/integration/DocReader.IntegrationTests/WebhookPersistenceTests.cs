using System.Text.Json;
using DocReader.Application.Abstractions;
using DocReader.Application.Documents;
using DocReader.Application.Options;
using DocReader.Application.Webhooks;
using DocReader.Domain.Catalog;
using DocReader.Domain.Documents;
using DocReader.Domain.Extractions;
using DocReader.Domain.Processing;
using DocReader.Domain.Retention;
using DocReader.Domain.Webhooks;
using DocReader.Infrastructure.Persistence;
using DocReader.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// Webhooks against a real PostgreSQL: the notification is queued in the same transaction as the change of status (and only for
/// the subscriptions that match), two workers never take the same delivery, a worker that lost its delivery changes nothing, and the
/// last failed attempt leaves a trace on the document.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class WebhookPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void RequireDatabase(PostgresFixture fixture) =>
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

    private static WebhookSubscription Subscription(string[] events, Guid? product = null, bool active = true, string url = "https://hooks.example.com/x") =>
        WebhookSubscription.Create(Guid.CreateVersion7(Now), url, "cifrado", events, product, active, Now);

    private async Task<T> InContext<T>(Func<DocReaderDbContext, Task<T>> action)
    {
        await using var context = fixture.CreateContext();

        return await action(context);
    }

    private async Task AddAsync(params WebhookSubscription[] subscriptions)
    {
        await using var context = fixture.CreateContext();
        context.WebhookSubscriptions.AddRange(subscriptions);
        await context.SaveChangesAsync(Ct);
    }

    private async Task<ProductService> AddProductAsync(string code)
    {
        var product = ProductService.Create(Guid.CreateVersion7(Now), code, code, true, Now);
        await using var context = fixture.CreateContext();
        await new ProductServiceRepository(context).AddAsync(product, Ct);

        return product;
    }

    private async Task<Document> SeedDocumentAsync(Guid? productServiceId = null, DocumentStatus status = DocumentStatus.Queued)
    {
        var id = Guid.CreateVersion7(Now);
        var document = Document.Accept(
            id, $"DOC-20260925-{Random.Shared.Next(1, 999_999):D6}", "doc.png", $"documents/2026/09/25/{id:D}/original.png", "image/png", 10,
            new string('f', 64), 1, UploadChannel.Api, null, null, Now, productServiceId, RetentionPolicyFor(Now));
        document.MarkQueued(Now);

        if (status == DocumentStatus.Completed)
        {
            document.MarkCompleted(Now);
        }

        await using var context = fixture.CreateContext();
        context.Documents.Add(document);
        await context.SaveChangesAsync(Ct);

        return document;
    }

    private static RetentionPolicy RetentionPolicyFor(DateTimeOffset now) =>
        RetentionPolicy.Create(RetentionPolicy.GlobalPolicyId, null, null, 365, now);

    private PostgresProcessingQueue QueueAt(DocReaderDbContext context, DateTimeOffset at) =>
        new(context, Options.Create(new ProcessingQueueOptions { WorkerName = "worker-test" }), new FakeTimeProvider(at), NullLogger<PostgresProcessingQueue>.Instance);

    private async Task<ProcessingJob> AcquireJobAsync(Guid documentId)
    {
        await using var context = fixture.CreateContext();
        var queue = QueueAt(context, Now);
        await queue.EnqueueAsync(documentId, Ct);

        return (await queue.AcquireNextAsync(Ct))!;
    }

    private static DocumentExtraction Extraction(Guid documentId, Guid jobId) =>
        DocumentExtraction.Create(
            documentId, jobId, "paddleocr", "PP-OCRv5 test", "rules-1.0.0", "br-cnh-1.0.0", 1, "texto", "[{\"pageNumber\":1,\"text\":\"texto\"}]",
            "{\"pages\":[{\"page\":1,\"raw\":{}}]}", "{}", 0.9m, Now, []);

    private async Task<bool> CompleteAsync(ProcessingJob job, Guid documentId, string detectedType = "BR_CNH")
    {
        await using var context = fixture.CreateContext();

        return await new DocumentProcessingStore(context, new FakeTimeProvider(Now.AddSeconds(30)))
            .CompleteAsync(job, Extraction(documentId, job.Id), detectedType, 0.95m, null, null, Ct);
    }

    private async Task<List<WebhookDelivery>> DeliveriesAsync(Guid? documentId = null) =>
        await InContext(context => context.WebhookDeliveries.AsNoTracking()
            .Where(delivery => documentId == null || delivery.DocumentId == documentId)
            .OrderBy(delivery => delivery.CreatedAt).ThenBy(delivery => delivery.Id)
            .ToListAsync(Ct));

    // ---- outbox ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Concluir_enfileira_a_notificacao_so_para_quem_pediu_o_evento_o_produto_e_esta_ativo()
    {
        RequireDatabase(fixture);

        var product = await AddProductAsync("conta-pj");
        var everyone = Subscription([WebhookEvents.DocumentCompleted]);
        var failedOnly = Subscription([WebhookEvents.DocumentFailed]);
        var inactive = Subscription([WebhookEvents.DocumentCompleted], active: false);
        var forProduct = Subscription([WebhookEvents.DocumentCompleted], product.Id);
        var forOtherProduct = Subscription([WebhookEvents.DocumentCompleted], (await AddProductAsync("outro")).Id);
        await AddAsync(everyone, failedOnly, inactive, forProduct, forOtherProduct);

        var document = await SeedDocumentAsync(product.Id);
        var job = await AcquireJobAsync(document.Id);

        Assert.True(await CompleteAsync(job, document.Id));

        var deliveries = await DeliveriesAsync(document.Id);
        Assert.Equal(
            new[] { everyone.Id, forProduct.Id }.Order(),
            deliveries.Select(delivery => delivery.SubscriptionId).Order());
        Assert.All(deliveries, delivery =>
        {
            Assert.Equal(WebhookEvents.DocumentCompleted, delivery.Event);
            Assert.Equal(WebhookDeliveryStatus.Pending, delivery.Status);
            Assert.Equal(0, delivery.AttemptCount);
            Assert.Equal(Now.AddSeconds(30), delivery.NextAttemptAt);
        });
    }

    [Fact]
    public async Task O_corpo_gravado_tem_os_campos_combinados_e_o_codigo_do_produto_e_o_tipo_detectado()
    {
        RequireDatabase(fixture);

        var product = await AddProductAsync("conta-pj");
        await AddAsync(Subscription([WebhookEvents.DocumentCompleted]));
        var document = await SeedDocumentAsync(product.Id);
        var job = await AcquireJobAsync(document.Id);
        await CompleteAsync(job, document.Id, "BR_CNPJ_CARD");

        var delivery = Assert.Single(await DeliveriesAsync());
        using var json = JsonDocument.Parse(delivery.Payload);
        var root = json.RootElement;

        Assert.Equal("document.completed", root.GetProperty("event").GetString());
        Assert.Equal(document.Id, root.GetProperty("documentId").GetGuid());
        Assert.Equal(document.Protocol, root.GetProperty("protocol").GetString());
        Assert.Equal("COMPLETED", root.GetProperty("status").GetString());
        Assert.Equal("BR_CNPJ_CARD", root.GetProperty("detectedDocumentType").GetString());
        Assert.Equal("CONTA-PJ", root.GetProperty("productServiceCode").GetString());
        Assert.EndsWith("Z", root.GetProperty("occurredAt").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("doc.png", delivery.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task O_payload_e_guardado_byte_a_byte_como_foi_montado()
    {
        RequireDatabase(fixture);

        await AddAsync(Subscription([WebhookEvents.DocumentCompleted]));
        var document = await SeedDocumentAsync();
        var job = await AcquireJobAsync(document.Id);
        await CompleteAsync(job, document.Id);

        var delivery = Assert.Single(await DeliveriesAsync());

        // jsonb would have added a space after each colon and reordered the keys.
        Assert.StartsWith("{\"event\":\"document.completed\",\"documentId\":", delivery.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sem_assinaturas_nada_e_enfileirado_e_o_documento_conclui_normalmente()
    {
        RequireDatabase(fixture);

        var document = await SeedDocumentAsync();
        var job = await AcquireJobAsync(document.Id);

        Assert.True(await CompleteAsync(job, document.Id));

        Assert.Empty(await DeliveriesAsync());
        Assert.Equal(DocumentStatus.Completed, await InContext(context => context.Documents.AsNoTracking().Where(item => item.Id == document.Id).Select(item => item.Status).SingleAsync(Ct)));
    }

    [Fact]
    public async Task Worker_que_perdeu_a_reserva_nao_conclui_o_documento_e_nao_enfileira_nada()
    {
        RequireDatabase(fixture);

        await AddAsync(Subscription([WebhookEvents.DocumentCompleted]));
        var document = await SeedDocumentAsync();
        var job = await AcquireJobAsync(document.Id);

        // Another worker takes the job over before the first finishes.
        await using (var other = fixture.CreateContext())
        {
            var options = new ProcessingQueueOptions { WorkerName = "worker-novo", JobLockTimeout = TimeSpan.FromMinutes(10) };
            await new PostgresProcessingQueue(other, Options.Create(options), new FakeTimeProvider(Now.AddMinutes(20)), NullLogger<PostgresProcessingQueue>.Instance)
                .AcquireNextAsync(Ct);
        }

        Assert.False(await CompleteAsync(job, document.Id));

        Assert.Empty(await DeliveriesAsync());
    }

    [Fact]
    public async Task Falha_definitiva_enfileira_document_failed_e_falha_com_retry_nao_enfileira()
    {
        RequireDatabase(fixture);

        await AddAsync(Subscription([WebhookEvents.DocumentFailed]));
        var document = await SeedDocumentAsync();
        var job = await AcquireJobAsync(document.Id);

        await using (var context = fixture.CreateContext())
        {
            var retry = await QueueAt(context, Now).FailAsync(job, new ProcessingError("OCR_UNAVAILABLE", "down", IsTransient: true), Ct);
            Assert.True(retry.WillRetry);
        }

        Assert.Empty(await DeliveriesAsync());

        ProcessingJob second;
        await using (var context = fixture.CreateContext())
        {
            second = (await QueueAt(context, Now.AddHours(1)).AcquireNextAsync(Ct))!;
            var final = await QueueAt(context, Now.AddHours(1)).FailAsync(second, new ProcessingError("UNREADABLE_DOCUMENT", "bad file", IsTransient: false), Ct);
            Assert.False(final.WillRetry);
        }

        var delivery = Assert.Single(await DeliveriesAsync());
        Assert.Equal(WebhookEvents.DocumentFailed, delivery.Event);
        using var json = JsonDocument.Parse(delivery.Payload);
        Assert.Equal("FAILED", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Expurgar_enfileira_document_purged_na_mesma_transacao_e_so_uma_vez()
    {
        RequireDatabase(fixture);

        var purgedOnly = Subscription([WebhookEvents.DocumentPurged]);
        await AddAsync(purgedOnly, Subscription([WebhookEvents.DocumentCompleted]));
        var document = await SeedDocumentAsync(status: DocumentStatus.Completed);
        await InContext(async context =>
        {
            await context.Database.ExecuteSqlRawAsync("UPDATE documents SET expires_at = {0} WHERE id = {1}", [Now.AddDays(-1), document.Id], Ct);

            return 0;
        });

        Assert.True(await InContext(context => new DocumentRepository(context).MarkPurgedAsync(document.Id, Now, Ct)));
        Assert.False(await InContext(context => new DocumentRepository(context).MarkPurgedAsync(document.Id, Now, Ct)));

        var delivery = Assert.Single(await DeliveriesAsync());
        Assert.Equal(purgedOnly.Id, delivery.SubscriptionId);
        Assert.Equal(WebhookEvents.DocumentPurged, delivery.Event);
        using var json = JsonDocument.Parse(delivery.Payload);
        Assert.Equal("PURGED", json.RootElement.GetProperty("status").GetString());
    }

    // ---- fila ---------------------------------------------------------------------------------------------------------

    private async Task<WebhookDelivery> QueuedDeliveryAsync(WebhookSubscription subscription, Document document, DateTimeOffset dueAt)
    {
        var delivery = WebhookDelivery.Create(Guid.CreateVersion7(dueAt), subscription.Id, document.Id, WebhookEvents.DocumentCompleted, "{\"event\":\"document.completed\"}", dueAt);
        await using var context = fixture.CreateContext();
        context.WebhookDeliveries.Add(delivery);
        await context.SaveChangesAsync(Ct);

        return delivery;
    }

    [Fact]
    public async Task Pega_a_mais_antiga_que_ja_venceu_e_conta_a_tentativa()
    {
        RequireDatabase(fixture);

        var subscription = Subscription([WebhookEvents.DocumentCompleted]);
        await AddAsync(subscription);
        var document = await SeedDocumentAsync();
        var older = await QueuedDeliveryAsync(subscription, document, Now.AddMinutes(-5));
        await QueuedDeliveryAsync(subscription, document, Now.AddMinutes(-1));
        await QueuedDeliveryAsync(subscription, document, Now.AddMinutes(10));

        await using var context = fixture.CreateContext();
        var claimed = await new WebhookDeliveryStore(context).ClaimNextAsync(Now, TimeSpan.FromMinutes(2), "w1", Ct);

        Assert.NotNull(claimed);
        Assert.Equal(older.Id, claimed.DeliveryId);
        Assert.Equal(1, claimed.Attempt);
        Assert.Equal("https://hooks.example.com/x", claimed.Url);
        Assert.Equal("cifrado", claimed.EncryptedSecret);
        Assert.True(claimed.SubscriptionActive);
    }

    [Fact]
    public async Task Entrega_que_ainda_nao_venceu_ou_ja_terminou_nao_e_pega()
    {
        RequireDatabase(fixture);

        var subscription = Subscription([WebhookEvents.DocumentCompleted]);
        await AddAsync(subscription);
        var document = await SeedDocumentAsync();
        await QueuedDeliveryAsync(subscription, document, Now.AddMinutes(10));

        await using var context = fixture.CreateContext();

        Assert.Null(await new WebhookDeliveryStore(context).ClaimNextAsync(Now, TimeSpan.FromMinutes(2), "w1", Ct));
    }

    [Fact]
    public async Task Duas_workers_ao_mesmo_tempo_nunca_pegam_a_mesma_entrega()
    {
        RequireDatabase(fixture);

        var subscription = Subscription([WebhookEvents.DocumentCompleted]);
        await AddAsync(subscription);
        var document = await SeedDocumentAsync();
        var expected = new List<Guid>();
        for (var index = 0; index < 20; index++)
        {
            expected.Add((await QueuedDeliveryAsync(subscription, document, Now.AddMinutes(-30 + index))).Id);
        }

        var claimed = new List<Guid>();
        var workers = Enumerable.Range(1, 4).Select(async index =>
        {
            await using var context = fixture.CreateContext();
            var store = new WebhookDeliveryStore(context);
            while (await store.ClaimNextAsync(Now, TimeSpan.FromMinutes(2), $"w{index}", Ct) is { } claim)
            {
                lock (claimed)
                {
                    claimed.Add(claim.DeliveryId);
                }
            }
        });
        await Task.WhenAll(workers);

        Assert.Equal(20, claimed.Count);
        Assert.Equal(20, claimed.Distinct().Count());
        Assert.Equal(expected.Order(), claimed.Order());
    }

    [Fact]
    public async Task Entrega_presa_por_um_worker_morto_volta_a_fila_depois_do_prazo_e_sem_o_prazo_nao()
    {
        RequireDatabase(fixture);

        var subscription = Subscription([WebhookEvents.DocumentCompleted]);
        await AddAsync(subscription);
        var document = await SeedDocumentAsync();
        var delivery = await QueuedDeliveryAsync(subscription, document, Now.AddMinutes(-10));

        await using (var first = fixture.CreateContext())
        {
            Assert.NotNull(await new WebhookDeliveryStore(first).ClaimNextAsync(Now, TimeSpan.FromMinutes(2), "morto", Ct));
        }

        await using (var early = fixture.CreateContext())
        {
            Assert.Null(await new WebhookDeliveryStore(early).ClaimNextAsync(Now.AddMinutes(1), TimeSpan.FromMinutes(2), "vivo", Ct));
        }

        await using var late = fixture.CreateContext();
        var taken = await new WebhookDeliveryStore(late).ClaimNextAsync(Now.AddMinutes(3), TimeSpan.FromMinutes(2), "vivo", Ct);

        Assert.Equal(delivery.Id, taken!.DeliveryId);
        Assert.Equal(2, taken.Attempt);
    }

    [Fact]
    public async Task So_quem_pegou_a_tentativa_grava_o_resultado()
    {
        RequireDatabase(fixture);

        var subscription = Subscription([WebhookEvents.DocumentCompleted]);
        await AddAsync(subscription);
        var document = await SeedDocumentAsync();
        var delivery = await QueuedDeliveryAsync(subscription, document, Now.AddMinutes(-10));

        await using (var contextA = fixture.CreateContext())
        {
            await new WebhookDeliveryStore(contextA).ClaimNextAsync(Now, TimeSpan.FromMinutes(2), "a", Ct);
        }

        // Another worker takes over the stale delivery: it is now on attempt 2 and belongs to "b".
        await using (var contextB = fixture.CreateContext())
        {
            await new WebhookDeliveryStore(contextB).ClaimNextAsync(Now.AddMinutes(5), TimeSpan.FromMinutes(2), "b", Ct);
        }

        await using var stale = fixture.CreateContext();
        var store = new WebhookDeliveryStore(stale);

        Assert.False(await store.RecordSuccessAsync(delivery.Id, "a", 1, 200, Now.AddMinutes(6), Ct));
        Assert.False(await store.RecordFailureAsync(delivery.Id, "b", 1, 500, "HTTP_500", Now.AddMinutes(6), Now.AddMinutes(7), Ct));

        var stored = (await DeliveriesAsync()).Single();
        Assert.Equal(WebhookDeliveryStatus.Pending, stored.Status);
        Assert.Equal("b", stored.LockedBy);

        Assert.True(await store.RecordSuccessAsync(delivery.Id, "b", 2, 200, Now.AddMinutes(6), Ct));
        Assert.Equal(WebhookDeliveryStatus.Succeeded, (await DeliveriesAsync()).Single().Status);
    }

    [Fact]
    public async Task Falha_com_retry_reagenda_e_solta_a_reserva()
    {
        RequireDatabase(fixture);

        var subscription = Subscription([WebhookEvents.DocumentCompleted]);
        await AddAsync(subscription);
        var document = await SeedDocumentAsync();
        var delivery = await QueuedDeliveryAsync(subscription, document, Now.AddMinutes(-1));

        await using (var claim = fixture.CreateContext())
        {
            await new WebhookDeliveryStore(claim).ClaimNextAsync(Now, TimeSpan.FromMinutes(2), "w", Ct);
        }

        await using (var record = fixture.CreateContext())
        {
            Assert.True(await new WebhookDeliveryStore(record).RecordFailureAsync(delivery.Id, "w", 1, 503, "HTTP_503", Now, Now.AddSeconds(10), Ct));
        }

        var stored = (await DeliveriesAsync()).Single();
        Assert.Equal(WebhookDeliveryStatus.Pending, stored.Status);
        Assert.Equal(Now.AddSeconds(10), stored.NextAttemptAt);
        Assert.Equal(503, stored.LastStatusCode);
        Assert.Equal("HTTP_503", stored.LastError);
        Assert.Null(stored.LockedBy);

        // Not due yet, then due.
        await using var early = fixture.CreateContext();
        Assert.Null(await new WebhookDeliveryStore(early).ClaimNextAsync(Now.AddSeconds(5), TimeSpan.FromMinutes(2), "w", Ct));
        await using var due = fixture.CreateContext();
        Assert.Equal(2, (await new WebhookDeliveryStore(due).ClaimNextAsync(Now.AddSeconds(11), TimeSpan.FromMinutes(2), "w", Ct))!.Attempt);
    }

    [Fact]
    public async Task A_ultima_falha_encerra_a_entrega_e_deixa_o_evento_no_historico_do_documento()
    {
        RequireDatabase(fixture);

        var subscription = Subscription([WebhookEvents.DocumentCompleted], url: "https://hooks.example.com/token-privado");
        await AddAsync(subscription);
        var document = await SeedDocumentAsync(status: DocumentStatus.Completed);
        var delivery = await QueuedDeliveryAsync(subscription, document, Now.AddMinutes(-1));

        await using (var claim = fixture.CreateContext())
        {
            await new WebhookDeliveryStore(claim).ClaimNextAsync(Now, TimeSpan.FromMinutes(2), "w", Ct);
        }

        await using (var record = fixture.CreateContext())
        {
            Assert.True(await new WebhookDeliveryStore(record).RecordFailureAsync(delivery.Id, "w", 1, null, "TIMEOUT", Now, null, Ct));
        }

        var stored = (await DeliveriesAsync()).Single();
        Assert.Equal(WebhookDeliveryStatus.Failed, stored.Status);
        Assert.Equal("TIMEOUT", stored.LastError);
        Assert.NotNull(stored.CompletedAt);

        await using var read = fixture.CreateContext();
        var loaded = await new DocumentRepository(read).FindByIdAsync(document.Id, includeEvents: true, Ct);
        var failed = Assert.Single(loaded!.Events, entry => entry.EventType == DocumentEventTypes.WebhookDeliveryFailed);
        Assert.Contains($"subscription={subscription.Id}", failed.Details, StringComparison.Ordinal);
        Assert.Contains("error=TIMEOUT", failed.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("token-privado", failed.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("hooks.example.com", failed.Details, StringComparison.Ordinal);
        Assert.Equal(DocumentStatus.Completed, loaded.Status);
    }

    [Fact]
    public async Task Cancelar_encerra_a_entrega_sem_evento_no_documento()
    {
        RequireDatabase(fixture);

        var subscription = Subscription([WebhookEvents.DocumentCompleted], active: false);
        await AddAsync(subscription);
        var document = await SeedDocumentAsync(status: DocumentStatus.Completed);
        var delivery = await QueuedDeliveryAsync(subscription, document, Now.AddMinutes(-1));

        await using (var claim = fixture.CreateContext())
        {
            var claimed = await new WebhookDeliveryStore(claim).ClaimNextAsync(Now, TimeSpan.FromMinutes(2), "w", Ct);
            Assert.False(claimed!.SubscriptionActive);
        }

        await using (var cancel = fixture.CreateContext())
        {
            Assert.True(await new WebhookDeliveryStore(cancel).CancelAsync(delivery.Id, "w", 1, Now, Ct));
        }

        Assert.Equal(WebhookDeliveryStatus.Cancelled, (await DeliveriesAsync()).Single().Status);
        await using var read = fixture.CreateContext();
        Assert.DoesNotContain((await new DocumentRepository(read).FindByIdAsync(document.Id, true, Ct))!.Events, entry => entry.EventType == DocumentEventTypes.WebhookDeliveryFailed);
    }

    // ---- assinaturas -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Excluir_a_assinatura_leva_junto_o_que_estava_na_fila()
    {
        RequireDatabase(fixture);

        var subscription = Subscription([WebhookEvents.DocumentCompleted]);
        await AddAsync(subscription);
        var document = await SeedDocumentAsync();
        await QueuedDeliveryAsync(subscription, document, Now);

        await using (var context = fixture.CreateContext())
        {
            var repository = new WebhookSubscriptionRepository(context);
            await repository.RemoveAsync((await repository.FindByIdAsync(subscription.Id, Ct))!, Ct);
        }

        Assert.Empty(await DeliveriesAsync());
    }

    [Fact]
    public async Task Excluir_o_documento_leva_junto_as_notificacoes_dele()
    {
        RequireDatabase(fixture);

        var subscription = Subscription([WebhookEvents.DocumentCompleted]);
        await AddAsync(subscription);
        var document = await SeedDocumentAsync();
        await QueuedDeliveryAsync(subscription, document, Now);

        await InContext(context => new DocumentRepository(context).DeleteAsync(document.Id, Ct));

        Assert.Empty(await DeliveriesAsync());
        Assert.Single(await InContext(context => context.WebhookSubscriptions.AsNoTracking().ToListAsync(Ct)));
    }

    [Fact]
    public async Task Assinatura_grava_eventos_como_array_filtra_e_lista_as_entregas_da_mais_nova_para_a_mais_antiga()
    {
        RequireDatabase(fixture);

        var product = await AddProductAsync("conta-pj");
        var active = Subscription([WebhookEvents.DocumentCompleted, WebhookEvents.DocumentPurged], product.Id);
        var inactive = Subscription([WebhookEvents.DocumentFailed], active: false);
        await AddAsync(active, inactive);
        var document = await SeedDocumentAsync();
        var oldest = await QueuedDeliveryAsync(active, document, Now.AddMinutes(-3));
        var newest = await QueuedDeliveryAsync(active, document, Now.AddMinutes(-1));

        await using var context = fixture.CreateContext();
        var repository = new WebhookSubscriptionRepository(context);

        var loaded = await repository.FindByIdAsync(active.Id, Ct);
        Assert.Equal([WebhookEvents.DocumentCompleted, WebhookEvents.DocumentPurged], loaded!.Events);
        Assert.Equal("CONTA-PJ", loaded.ProductService!.Code);

        Assert.Equal([active.Id], (await repository.ListAsync(new WebhookSubscriptionFilter(true, null, 1, 10), Ct)).Items.Select(item => item.Id));
        Assert.Equal([inactive.Id], (await repository.ListAsync(new WebhookSubscriptionFilter(false, null, 1, 10), Ct)).Items.Select(item => item.Id));
        Assert.Equal([active.Id], (await repository.ListAsync(new WebhookSubscriptionFilter(null, product.Id, 1, 10), Ct)).Items.Select(item => item.Id));

        var deliveries = await repository.ListDeliveriesAsync(active.Id, 1, 10, Ct);
        Assert.Equal([newest.Id, oldest.Id], deliveries.Items.Select(item => item.Id));
        Assert.Equal(2, deliveries.TotalCount);
    }

    [Fact]
    public async Task Produto_com_assinatura_conta_como_referenciado_e_o_banco_impede_a_exclusao()
    {
        RequireDatabase(fixture);

        var product = await AddProductAsync("conta-pj");
        await AddAsync(Subscription([WebhookEvents.DocumentCompleted], product.Id));

        await using var context = fixture.CreateContext();
        var repository = new ProductServiceRepository(context);

        Assert.True(await repository.IsReferencedAsync(product.Id, Ct));
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await repository.RemoveAsync((await repository.FindByIdAsync(product.Id, Ct))!, Ct));
    }

    [Fact]
    public async Task O_segredo_fica_no_banco_como_o_texto_cifrado_recebido_e_nao_em_claro()
    {
        RequireDatabase(fixture);

        var subscription = WebhookSubscription.Create(Guid.CreateVersion7(Now), "https://hooks.example.com/x", "{\"v\":1,\"alg\":\"AES-256-GCM\",\"n\":\"x\",\"c\":\"y\",\"t\":\"z\"}", [WebhookEvents.DocumentCompleted], null, true, Now);
        await AddAsync(subscription);

        var stored = await InContext(context => context.WebhookSubscriptions.AsNoTracking().SingleAsync(item => item.Id == subscription.Id, Ct));

        Assert.Equal(subscription.EncryptedSecret, stored.EncryptedSecret);
    }
}
