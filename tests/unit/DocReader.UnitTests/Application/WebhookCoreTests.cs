using System.Net;
using System.Text;
using System.Text.Json;
using DocReader.Application.Errors;
using DocReader.Application.Webhooks;
using DocReader.Domain.Documents;
using DocReader.Domain.Webhooks;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>A assinatura, a regra de rede privada, a validação da URL e o corpo da notificação.</summary>
public sealed class WebhookCoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, 123, TimeSpan.Zero);

    // ---- assinatura --------------------------------------------------------------------------------------------

    [Fact]
    public void A_assinatura_bate_com_o_vetor_conhecido_de_hmac_sha256()
    {
        // RFC 4231, caso 2: chave "Jefe", dado "what do ya want for nothing?".
        var signature = WebhookSignature.Compute("Jefe", Encoding.UTF8.GetBytes("what do ya want for nothing?"));

        Assert.Equal("sha256=5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", signature);
    }

    [Fact]
    public void Assinatura_e_verificada_e_qualquer_alteracao_do_corpo_ou_da_chave_reprova()
    {
        var body = Encoding.UTF8.GetBytes("{\"event\":\"document.completed\"}");
        var signature = WebhookSignature.Compute("segredo-com-16-caracteres", body);

        Assert.True(WebhookSignature.Verify("segredo-com-16-caracteres", body, signature));
        Assert.False(WebhookSignature.Verify("outro-segredo-16-chars", body, signature));
        Assert.False(WebhookSignature.Verify("segredo-com-16-caracteres", Encoding.UTF8.GetBytes("{\"event\":\"document.failed\"}"), signature));
        Assert.False(WebhookSignature.Verify("segredo-com-16-caracteres", body, null));
        Assert.False(WebhookSignature.Verify("segredo-com-16-caracteres", body, "sha256=00"));
        Assert.False(WebhookSignature.Verify("segredo-com-16-caracteres", body, signature[WebhookSignature.Prefix.Length..]));
    }

    [Fact]
    public void A_assinatura_cobre_os_bytes_exatos_incluindo_espacos()
    {
        var compact = Encoding.UTF8.GetBytes("{\"a\":1}");
        var spaced = Encoding.UTF8.GetBytes("{\"a\": 1}");

        Assert.NotEqual(WebhookSignature.Compute("k-de-dezesseis-bytes", compact), WebhookSignature.Compute("k-de-dezesseis-bytes", spaced));
    }

    // ---- rede privada ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.20.30")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("172.18.0.3")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.1.2.3")]
    [InlineData("::ffff:169.254.169.254")]
    public void Enderecos_locais_e_privados_sao_reconhecidos(string address) =>
        Assert.True(PrivateNetwork.IsPrivate(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("11.0.0.1")]
    [InlineData("192.169.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Enderecos_publicos_passam(string address) =>
        Assert.False(PrivateNetwork.IsPrivate(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("api.localhost", true)]
    [InlineData("receiver", true)]
    [InlineData("printer.local", true)]
    [InlineData("service.internal", true)]
    [InlineData("localhost.", true)]
    [InlineData("webhook.site", false)]
    [InlineData("hooks.example.com", false)]
    public void Nomes_obviamente_locais_sao_reconhecidos_antes_do_dns(string host, bool local) =>
        Assert.Equal(local, PrivateNetwork.IsLocalName(host));

    // ---- politica de URL -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://webhook.site/6c0e2f6a-1b7d-4c8e-9a5f-3d2b1c0e9f8a")]
    [InlineData("http://hooks.example.com/notify?token=abc")]
    [InlineData("  https://8.8.8.8/hook  ")]
    [InlineData("https://hooks.example.com:8443/x")]
    public void Url_publica_e_aceita_e_normalizada(string url)
    {
        var accepted = WebhookUrlPolicy.Validate(url, allowPrivateNetworks: false);

        Assert.True(Uri.IsWellFormedUriString(accepted, UriKind.Absolute));
        Assert.Equal(accepted.Trim(), accepted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nao-e-url")]
    [InlineData("/caminho/relativo")]
    [InlineData("ftp://hooks.example.com/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://usuario:senha@hooks.example.com/x")]
    [InlineData("https://usuario@hooks.example.com/x")]
    public void Url_invalida_e_recusada(string? url)
    {
        var error = Assert.Throws<RequestValidationException>(() => WebhookUrlPolicy.Validate(url, false));

        Assert.Equal("INVALID_WEBHOOK_URL", error.ErrorCode);
    }

    [Theory]
    [InlineData("http://localhost:9000/hook")]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://[::1]/hook")]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("http://192.168.0.10:8080/hook")]
    [InlineData("http://172.18.0.4/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://receiver:9000/hook")]
    [InlineData("http://api.internal/hook")]
    [InlineData("http://[::ffff:127.0.0.1]/hook")]
    public void Destino_local_ou_privado_e_recusado_por_padrao(string url)
    {
        var error = Assert.Throws<RequestValidationException>(() => WebhookUrlPolicy.Validate(url, allowPrivateNetworks: false));

        Assert.Equal("INVALID_WEBHOOK_URL", error.ErrorCode);
        Assert.Contains("WEBHOOK_ALLOW_PRIVATE_NETWORKS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Destino_privado_e_aceito_quando_o_ambiente_permite()
    {
        Assert.NotEmpty(WebhookUrlPolicy.Validate("http://receiver:9000/hook", allowPrivateNetworks: true));
        Assert.NotEmpty(WebhookUrlPolicy.Validate("http://127.0.0.1/hook", allowPrivateNetworks: true));
    }

    [Fact]
    public void Url_acima_do_limite_e_recusada()
    {
        var url = "https://hooks.example.com/" + new string('a', WebhookSubscription.MaxUrlLength);

        Assert.Throws<RequestValidationException>(() => WebhookUrlPolicy.Validate(url, false));
    }

    // ---- a assinatura casa evento e produto ----------------------------------------------------------------------

    private static WebhookSubscription Subscription(string[] events, Guid? product = null, bool active = true) =>
        WebhookSubscription.Create(Guid.CreateVersion7(Now), "https://hooks.example.com/x", "cifrado", events, product, active, Now);

    [Fact]
    public void A_assinatura_recebe_so_os_eventos_pedidos()
    {
        var subscription = Subscription([WebhookEvents.DocumentCompleted, WebhookEvents.DocumentFailed]);

        Assert.True(subscription.Wants(WebhookEvents.DocumentCompleted, null));
        Assert.True(subscription.Wants(WebhookEvents.DocumentFailed, null));
        Assert.False(subscription.Wants(WebhookEvents.DocumentPurged, null));
    }

    [Fact]
    public void Filtro_de_produto_so_deixa_passar_documentos_do_produto()
    {
        var product = Guid.CreateVersion7(Now);
        var filtered = Subscription([WebhookEvents.DocumentCompleted], product);
        var open = Subscription([WebhookEvents.DocumentCompleted]);

        Assert.True(filtered.Wants(WebhookEvents.DocumentCompleted, product));
        Assert.False(filtered.Wants(WebhookEvents.DocumentCompleted, Guid.CreateVersion7(Now.AddSeconds(1))));
        Assert.False(filtered.Wants(WebhookEvents.DocumentCompleted, null));
        Assert.True(open.Wants(WebhookEvents.DocumentCompleted, product));
        Assert.True(open.Wants(WebhookEvents.DocumentCompleted, null));
    }

    [Fact]
    public void Assinatura_inativa_nao_recebe_nada()
    {
        Assert.False(Subscription([WebhookEvents.DocumentCompleted], active: false).Wants(WebhookEvents.DocumentCompleted, null));
    }

    [Fact]
    public void Eventos_repetidos_sao_reduzidos_e_ordenados()
    {
        var subscription = Subscription([WebhookEvents.DocumentPurged, WebhookEvents.DocumentCompleted, " document.completed "]);

        Assert.Equal([WebhookEvents.DocumentCompleted, WebhookEvents.DocumentPurged], subscription.Events);
    }

    [Theory]
    [InlineData("document.completed", true)]
    [InlineData("document.failed", true)]
    [InlineData("document.purged", true)]
    [InlineData("document.deleted", false)]
    [InlineData("DOCUMENT.COMPLETED", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Somente_os_tres_eventos_sao_validos(string? name, bool valid) =>
        Assert.Equal(valid, WebhookEvents.IsValid(name));

    // ---- corpo ---------------------------------------------------------------------------------------------------

    private static Document CompletedDocument(Guid? productId = null)
    {
        var id = Guid.CreateVersion7(Now);
        var document = Document.Accept(
            id, "DOC-20260925-000007", "nome-secreto-do-arquivo.png", $"documents/{id:D}/original.png", "image/png", 10, new string('a', 64), 1,
            UploadChannel.Api, "REF-INTERNA-999", null, Now, productId);
        document.MarkQueued(Now);
        document.RecordClassification("BR_CNH", 0.95m, Now);
        document.MarkCompleted(Now);

        return document;
    }

    [Fact]
    public void O_corpo_traz_os_campos_combinados_com_datas_em_utc()
    {
        var document = CompletedDocument();

        var body = WebhookPayload.Build(WebhookEvents.DocumentCompleted, document, "CONTA-PJ", Now);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("document.completed", root.GetProperty("event").GetString());
        Assert.Equal(document.Id, root.GetProperty("documentId").GetGuid());
        Assert.Equal("DOC-20260925-000007", root.GetProperty("protocol").GetString());
        Assert.Equal("COMPLETED", root.GetProperty("status").GetString());
        Assert.Equal("BR_CNH", root.GetProperty("detectedDocumentType").GetString());
        Assert.Equal("CONTA-PJ", root.GetProperty("productServiceCode").GetString());
        Assert.Equal("2026-09-25T12:00:00.123Z", root.GetProperty("occurredAt").GetString());
    }

    [Fact]
    public void Sem_tipo_ou_produto_os_campos_vao_nulos_e_nao_somem()
    {
        var id = Guid.CreateVersion7(Now);
        var document = Document.Accept(id, "DOC-20260925-000008", "a.png", $"documents/{id:D}", "image/png", 10, new string('b', 64), 1, UploadChannel.Api, null, null, Now);
        document.MarkQueued(Now);
        document.MarkFailed("OCR_UNAVAILABLE", "down", Now);

        using var json = JsonDocument.Parse(WebhookPayload.Build(WebhookEvents.DocumentFailed, document, null, Now));

        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("detectedDocumentType").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("productServiceCode").ValueKind);
        Assert.Equal("FAILED", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void O_corpo_nunca_carrega_conteudo_nome_de_arquivo_nem_referencia_externa()
    {
        var body = WebhookPayload.Build(WebhookEvents.DocumentCompleted, CompletedDocument(), "CONTA-PJ", Now);

        Assert.DoesNotContain("nome-secreto", body, StringComparison.Ordinal);
        Assert.DoesNotContain("REF-INTERNA", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sha256", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storage", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DocumentStatus.Completed, "document.completed")]
    [InlineData(DocumentStatus.Failed, "document.failed")]
    [InlineData(DocumentStatus.Purged, "document.purged")]
    public void Cada_estado_final_tem_seu_evento(DocumentStatus status, string expected) =>
        Assert.Equal(expected, WebhookPayload.EventFor(status));

    [Theory]
    [InlineData(DocumentStatus.Received)]
    [InlineData(DocumentStatus.Queued)]
    [InlineData(DocumentStatus.OcrRunning)]
    [InlineData(DocumentStatus.Extracting)]
    [InlineData(DocumentStatus.Rejected)]
    public void Estado_intermediario_nao_gera_evento(DocumentStatus status) =>
        Assert.Null(WebhookPayload.EventFor(status));

    [Fact]
    public void O_mesmo_corpo_e_gerado_para_o_mesmo_documento_bytes_estaveis_para_os_retries()
    {
        var document = CompletedDocument();

        Assert.Equal(
            WebhookPayload.Build(WebhookEvents.DocumentCompleted, document, "P", Now),
            WebhookPayload.Build(WebhookEvents.DocumentCompleted, document, "P", Now));
    }
}
