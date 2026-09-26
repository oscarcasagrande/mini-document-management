using System.Text;
using DocReader.Application.Errors;
using DocReader.Application.Options;
using DocReader.Application.Webhooks;
using DocReader.Domain.Catalog;
using DocReader.Domain.Webhooks;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>Administração das assinaturas e o despacho de uma notificação: sucesso, retries de 10s/30s/90s e falha final.</summary>
public sealed class WebhookServicesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryWebhookSubscriptionStore _subscriptions = new();
    private readonly InMemoryProductServiceStore _products = new();
    private readonly FakeSecretProtector _protector = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly WebhookOptions _options = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebhookSubscriptionService Service(bool allowPrivate = false) => new(
        _subscriptions,
        _products,
        _protector,
        Options.Create(new WebhookOptions { AllowPrivateNetworks = allowPrivate }),
        _clock,
        NullLogger<WebhookSubscriptionService>.Instance);

    private static readonly string[] Completed = [WebhookEvents.DocumentCompleted];

    // ---- administração -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Criar_sem_segredo_gera_um_e_o_devolve_uma_vez_guardando_so_o_cifrado()
    {
        var created = await Service().CreateAsync("https://hooks.example.com/x", null, Completed, null, true, Ct);

        Assert.NotNull(created.GeneratedSecret);
        Assert.Equal(64, created.GeneratedSecret!.Length);
        Assert.DoesNotContain(created.GeneratedSecret, created.Subscription.EncryptedSecret, StringComparison.Ordinal);
        Assert.Equal(created.GeneratedSecret, _protector.Unprotect(created.Subscription.EncryptedSecret));
    }

    [Fact]
    public async Task Criar_com_segredo_proprio_nao_o_devolve_e_o_guarda_cifrado()
    {
        var created = await Service().CreateAsync("https://hooks.example.com/x", "meu-segredo-de-16-ou-mais", Completed, null, true, Ct);

        Assert.Null(created.GeneratedSecret);
        Assert.Equal("meu-segredo-de-16-ou-mais", _protector.Unprotect(created.Subscription.EncryptedSecret));
        Assert.DoesNotContain("meu-segredo", created.Subscription.EncryptedSecret, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("curto")]
    [InlineData("123456789012345")]
    public async Task Segredo_curto_demais_e_recusado(string secret)
    {
        var error = await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service().CreateAsync("https://hooks.example.com/x", secret, Completed, null, true, Ct));

        Assert.Equal("INVALID_WEBHOOK_SECRET", error.ErrorCode);
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Segredo_longo_demais_e_recusado()
    {
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service().CreateAsync("https://hooks.example.com/x", new string('s', WebhookSubscription.MaximumSecretLength + 1), Completed, null, true, Ct));
    }

    [Theory]
    [InlineData(null)]
    public async Task Sem_eventos_e_erro(string[]? events)
    {
        var error = await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service().CreateAsync("https://hooks.example.com/x", null, events, null, true, Ct));

        Assert.Equal("INVALID_WEBHOOK_EVENTS", error.ErrorCode);
    }

    [Fact]
    public async Task Lista_vazia_de_eventos_e_evento_desconhecido_sao_erro_e_a_mensagem_lista_os_validos()
    {
        var empty = await Assert.ThrowsAsync<RequestValidationException>(() => Service().CreateAsync("https://hooks.example.com/x", null, [], null, true, Ct));
        var unknown = await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service().CreateAsync("https://hooks.example.com/x", null, ["document.completed", "document.deleted"], null, true, Ct));

        Assert.Equal("INVALID_WEBHOOK_EVENTS", empty.ErrorCode);
        Assert.Contains("document.deleted", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("document.purged", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Url_privada_e_recusada_a_menos_que_o_ambiente_permita()
    {
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service().CreateAsync("http://receiver:9000/hook", null, Completed, null, true, Ct));

        var allowed = await Service(allowPrivate: true).CreateAsync("http://receiver:9000/hook", null, Completed, null, true, Ct);

        Assert.Equal("http://receiver:9000/hook", allowed.Subscription.Url);
    }

    [Fact]
    public async Task Filtro_de_produto_exige_produto_existente()
    {
        var error = await Assert.ThrowsAsync<UnprocessableRequestException>(() =>
            Service().CreateAsync("https://hooks.example.com/x", null, Completed, Guid.NewGuid(), true, Ct));

        Assert.Equal("PRODUCT_SERVICE_NOT_FOUND", error.ErrorCode);
        Assert.Empty(_subscriptions.Items);
    }

    [Fact]
    public async Task Filtro_de_produto_existente_e_guardado()
    {
        var product = ProductService.Create(Guid.CreateVersion7(Now), "CONTA-PJ", "Conta PJ", true, Now);
        await _products.AddAsync(product, Ct);

        var created = await Service().CreateAsync("https://hooks.example.com/x", null, Completed, product.Id, true, Ct);

        Assert.Equal(product.Id, created.Subscription.ProductServiceId);
    }

    [Fact]
    public async Task Atualizar_troca_url_eventos_filtro_e_ativo_e_mantem_o_segredo_quando_nao_enviado()
    {
        var created = await Service().CreateAsync("https://hooks.example.com/x", "segredo-original-16-chars", Completed, null, true, Ct);
        var before = created.Subscription.EncryptedSecret;

        var updated = await Service().UpdateAsync(
            created.Subscription.Id, "https://hooks.example.com/y", null, [WebhookEvents.DocumentFailed, WebhookEvents.DocumentPurged], null, false, Ct);

        Assert.Equal("https://hooks.example.com/y", updated.Url);
        Assert.Equal([WebhookEvents.DocumentFailed, WebhookEvents.DocumentPurged], updated.Events);
        Assert.False(updated.Active);
        Assert.Equal(before, updated.EncryptedSecret);
    }

    [Fact]
    public async Task Atualizar_com_segredo_rotaciona_a_chave()
    {
        var created = await Service().CreateAsync("https://hooks.example.com/x", "segredo-original-16-chars", Completed, null, true, Ct);

        await Service().UpdateAsync(created.Subscription.Id, "https://hooks.example.com/x", "segredo-novo-com-16-chars", Completed, null, true, Ct);

        Assert.Equal("segredo-novo-com-16-chars", _protector.Unprotect(created.Subscription.EncryptedSecret));
    }

    [Fact]
    public async Task Atualizar_com_dados_invalidos_nao_muda_nada()
    {
        var created = await Service().CreateAsync("https://hooks.example.com/x", null, Completed, null, true, Ct);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service().UpdateAsync(created.Subscription.Id, "https://hooks.example.com/y", null, ["document.deleted"], null, true, Ct));

        Assert.Equal("https://hooks.example.com/x", created.Subscription.Url);
    }

    [Fact]
    public async Task Excluir_remove_a_assinatura_e_o_que_estava_na_fila()
    {
        var created = await Service().CreateAsync("https://hooks.example.com/x", null, Completed, null, true, Ct);
        _subscriptions.Deliveries.Add(WebhookDelivery.Create(Guid.CreateVersion7(Now), created.Subscription.Id, Guid.NewGuid(), "document.completed", "{}", Now));

        await Service().DeleteAsync(created.Subscription.Id, Ct);

        Assert.Empty(_subscriptions.Items);
        Assert.Empty(_subscriptions.Deliveries);
    }

    [Fact]
    public async Task Assinatura_inexistente_e_not_found_em_todas_as_operacoes()
    {
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service().GetAsync(id, Ct));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service().DeleteAsync(id, Ct));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service().ListDeliveriesAsync(id, 1, 10, Ct));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Service().UpdateAsync(id, "https://hooks.example.com/x", null, Completed, null, true, Ct));
    }

    // ---- despacho -------------------------------------------------------------------------------------------------

    private const string SecretText = "segredo-de-assinatura-32-bytes!!";

    private ClaimedDelivery Claim(int attempt = 1, bool active = true, string? encryptedSecret = null) => new(
        Guid.CreateVersion7(Now),
        Guid.CreateVersion7(Now.AddSeconds(1)),
        Guid.CreateVersion7(Now.AddSeconds(2)),
        WebhookEvents.DocumentCompleted,
        "{\"event\":\"document.completed\",\"documentId\":\"x\"}",
        attempt,
        "https://hooks.example.com/token-privado-na-url",
        encryptedSecret ?? _protector.Protect(SecretText),
        active);

    private WebhookDispatchService Dispatcher(ScriptedDeliveryStore store, ScriptedSender sender, ILogger<WebhookDispatchService>? logger = null) => new(
        store, sender, _protector, Options.Create(_options), _clock, logger ?? NullLogger<WebhookDispatchService>.Instance);

    [Fact]
    public async Task Sem_nada_na_fila_nao_faz_nada()
    {
        var sender = new ScriptedSender(_ => new WebhookSendResult(200, null));

        Assert.False(await Dispatcher(new ScriptedDeliveryStore(), sender).DispatchNextAsync("w", Ct));
        Assert.Empty(sender.Requests);
    }

    [Fact]
    public async Task Resposta_2xx_e_sucesso_e_a_requisicao_leva_corpo_assinatura_e_cabecalhos()
    {
        var store = new ScriptedDeliveryStore();
        var claim = Claim();
        store.ToClaim.Enqueue(claim);
        var sender = new ScriptedSender(_ => new WebhookSendResult(204, null));

        Assert.True(await Dispatcher(store, sender).DispatchNextAsync("w", Ct));

        var request = Assert.Single(sender.Requests);
        Assert.Equal(claim.Url, request.Url);
        Assert.Equal(claim.Payload, Encoding.UTF8.GetString(request.Body));
        Assert.Equal(WebhookSignature.Compute(SecretText, request.Body), request.Signature);
        Assert.True(WebhookSignature.Verify(SecretText, request.Body, request.Signature));
        Assert.Equal("document.completed", request.Event);
        Assert.Equal(claim.DeliveryId, request.DeliveryId);
        Assert.Equal(1, request.Attempt);
        Assert.Equal([(claim.DeliveryId, 1, 204)], store.Successes);
        Assert.Empty(store.Failures);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(299)]
    public async Task Qualquer_2xx_conta_como_entregue(int status)
    {
        var store = new ScriptedDeliveryStore();
        store.ToClaim.Enqueue(Claim());

        await Dispatcher(store, new ScriptedSender(_ => new WebhookSendResult(status, null))).DispatchNextAsync("w", Ct);

        Assert.Single(store.Successes);
    }

    [Theory]
    [InlineData(199, "HTTP_199")]
    [InlineData(301, "HTTP_301")]
    [InlineData(302, "HTTP_302")]
    [InlineData(400, "HTTP_400")]
    [InlineData(404, "HTTP_404")]
    [InlineData(500, "HTTP_500")]
    [InlineData(503, "HTTP_503")]
    public async Task Resposta_fora_de_2xx_e_falha_com_o_codigo_http(int status, string code)
    {
        var store = new ScriptedDeliveryStore();
        store.ToClaim.Enqueue(Claim());

        await Dispatcher(store, new ScriptedSender(_ => new WebhookSendResult(status, null))).DispatchNextAsync("w", Ct);

        var failure = Assert.Single(store.Failures);
        Assert.Equal(code, failure.Error);
        Assert.Equal(status, failure.StatusCode);
        Assert.Empty(store.Successes);
    }

    [Theory]
    [InlineData("TIMEOUT")]
    [InlineData("CONNECTION_FAILED")]
    [InlineData("BLOCKED_ADDRESS")]
    public async Task Falha_de_transporte_grava_o_codigo_sem_status(string code)
    {
        var store = new ScriptedDeliveryStore();
        store.ToClaim.Enqueue(Claim());

        await Dispatcher(store, new ScriptedSender(_ => new WebhookSendResult(null, code))).DispatchNextAsync("w", Ct);

        var failure = Assert.Single(store.Failures);
        Assert.Equal(code, failure.Error);
        Assert.Null(failure.StatusCode);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 30)]
    [InlineData(3, 90)]
    public async Task O_retry_espera_10_30_e_90_segundos_depois_de_cada_falha(int attempt, int seconds)
    {
        var store = new ScriptedDeliveryStore();
        store.ToClaim.Enqueue(Claim(attempt));

        await Dispatcher(store, new ScriptedSender(_ => new WebhookSendResult(503, null))).DispatchNextAsync("w", Ct);

        Assert.Equal(Now.AddSeconds(seconds), Assert.Single(store.Failures).Next);
    }

    [Fact]
    public async Task A_quarta_falha_e_definitiva_sem_proxima_tentativa()
    {
        var store = new ScriptedDeliveryStore();
        store.ToClaim.Enqueue(Claim(attempt: 4));

        await Dispatcher(store, new ScriptedSender(_ => new WebhookSendResult(503, null))).DispatchNextAsync("w", Ct);

        Assert.Null(Assert.Single(store.Failures).Next);
    }

    [Fact]
    public async Task Sucesso_no_retry_encerra_a_entrega()
    {
        var store = new ScriptedDeliveryStore();
        store.ToClaim.Enqueue(Claim(attempt: 3));

        await Dispatcher(store, new ScriptedSender(_ => new WebhookSendResult(200, null))).DispatchNextAsync("w", Ct);

        Assert.Equal(3, Assert.Single(store.Successes).Attempt);
        Assert.Empty(store.Failures);
    }

    [Fact]
    public async Task Os_atrasos_de_retry_sao_configuraveis()
    {
        _options.RetryDelays = [TimeSpan.FromSeconds(1)];
        var store = new ScriptedDeliveryStore();
        store.ToClaim.Enqueue(Claim(attempt: 1));
        store.ToClaim.Enqueue(Claim(attempt: 2));

        var sender = new ScriptedSender(_ => new WebhookSendResult(500, null));
        await Dispatcher(store, sender).DispatchNextAsync("w", Ct);
        await Dispatcher(store, sender).DispatchNextAsync("w", Ct);

        Assert.Equal(Now.AddSeconds(1), store.Failures[0].Next);
        Assert.Null(store.Failures[1].Next);
    }

    [Fact]
    public async Task Assinatura_desativada_depois_de_enfileirar_cancela_sem_enviar()
    {
        var store = new ScriptedDeliveryStore();
        var claim = Claim(active: false);
        store.ToClaim.Enqueue(claim);
        var sender = new ScriptedSender(_ => new WebhookSendResult(200, null));

        Assert.True(await Dispatcher(store, sender).DispatchNextAsync("w", Ct));

        Assert.Empty(sender.Requests);
        Assert.Equal([claim.DeliveryId], store.Cancelled);
    }

    [Fact]
    public async Task Segredo_ilegivel_falha_com_codigo_proprio_e_nada_e_enviado()
    {
        var store = new ScriptedDeliveryStore();
        store.ToClaim.Enqueue(Claim(encryptedSecret: "{\"outra\":\"chave\"}"));
        var sender = new ScriptedSender(_ => new WebhookSendResult(200, null));

        await Dispatcher(store, sender).DispatchNextAsync("w", Ct);

        Assert.Empty(sender.Requests);
        Assert.Equal("SECRET_UNREADABLE", Assert.Single(store.Failures).Error);
    }

    [Fact]
    public async Task Os_logs_nao_trazem_a_url_o_corpo_nem_o_segredo()
    {
        var logger = new CapturingLogger<WebhookDispatchService>();
        var store = new ScriptedDeliveryStore();
        store.ToClaim.Enqueue(Claim(attempt: 1));
        store.ToClaim.Enqueue(Claim(attempt: 4));
        store.ToClaim.Enqueue(Claim());
        var sender = new ScriptedSender(request => request.Attempt == 4 ? new WebhookSendResult(503, null) : new WebhookSendResult(200, null));

        var dispatcher = Dispatcher(store, sender, logger);
        await dispatcher.DispatchNextAsync("w", Ct);
        await dispatcher.DispatchNextAsync("w", Ct);
        await dispatcher.DispatchNextAsync("w", Ct);

        var text = string.Join('\n', logger.Messages);
        Assert.Contains("Webhook delivered", text, StringComparison.Ordinal);
        Assert.Contains("failed for good", text, StringComparison.Ordinal);
        Assert.DoesNotContain("token-privado", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hooks.example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretText, text, StringComparison.Ordinal);
        Assert.DoesNotContain("documentId\":\"x", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task O_mesmo_corpo_e_a_mesma_assinatura_em_todas_as_tentativas_e_o_id_da_entrega_nao_muda()
    {
        var store = new ScriptedDeliveryStore();
        var first = Claim(1);
        var retry = first with { Attempt = 2 };
        store.ToClaim.Enqueue(first);
        store.ToClaim.Enqueue(retry);
        var sender = new ScriptedSender(_ => new WebhookSendResult(500, null));

        await Dispatcher(store, sender).DispatchNextAsync("w", Ct);
        await Dispatcher(store, sender).DispatchNextAsync("w", Ct);

        Assert.Equal(sender.Requests[0].Body, sender.Requests[1].Body);
        Assert.Equal(sender.Requests[0].Signature, sender.Requests[1].Signature);
        Assert.Equal(sender.Requests[0].DeliveryId, sender.Requests[1].DeliveryId);
        Assert.Equal([1, 2], sender.Requests.Select(request => request.Attempt));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + " " + string.Join(' ', (state as IEnumerable<KeyValuePair<string, object?>>)?.Select(pair => $"{pair.Key}={pair.Value}") ?? []));
    }
}
