using System.Net;
using System.Text;
using DocReader.Application.Options;
using DocReader.Application.Webhooks;
using DocReader.Infrastructure.Webhooks;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocReader.UnitTests.Infrastructure;

/// <summary>
/// The HTTP sender against a real listener on this machine: what goes on the wire, what each kind of answer becomes, and the
/// refusal to connect to a private address. The listener is loopback, so the tests that send to it turn private networks on.
/// </summary>
public sealed class HttpWebhookSenderTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _prefix;
    private readonly List<CapturedRequest> _captured = [];
    private Func<HttpListenerContext, Task> _handler = context => { context.Response.StatusCode = 200; return Task.CompletedTask; };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public HttpWebhookSenderTests()
    {
        var port = FreePort();
        _prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _ = Task.Run(Serve);
    }

    public void Dispose() => _listener.Close();

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            lock (_captured)
            {
                _captured.Add(new CapturedRequest(
                    context.Request.HttpMethod,
                    context.Request.Url!.AbsolutePath,
                    body,
                    context.Request.ContentType,
                    context.Request.Headers.AllKeys.ToDictionary(key => key!, key => context.Request.Headers[key]!, StringComparer.OrdinalIgnoreCase)));
            }

            try
            {
                await _handler(context);
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private HttpWebhookSender Sender(bool allowPrivate, TimeSpan? timeout = null)
    {
        var options = Options.Create(new WebhookOptions { AllowPrivateNetworks = allowPrivate, RequestTimeout = timeout ?? TimeSpan.FromSeconds(5) });
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = (context, ct) => HttpWebhookSender.ConnectGuardedAsync(context, options.Value.AllowPrivateNetworks, ct)
        };

        return new HttpWebhookSender(new FixedClientFactory(handler), options);
    }

    private WebhookRequest Request(string path = "hook", Guid? deliveryId = null, int attempt = 1)
    {
        var body = Encoding.UTF8.GetBytes("{\"event\":\"document.completed\",\"documentId\":\"0199c1f0-7b3a-7a10-9c44-2f1d8e6b4a21\"}");

        return new WebhookRequest(
            _prefix + path,
            body,
            WebhookSignature.Compute("segredo-de-teste-com-16-chars", body),
            "document.completed",
            deliveryId ?? Guid.CreateVersion7(),
            attempt);
    }

    [Fact]
    public async Task O_post_leva_o_corpo_exato_a_assinatura_e_os_cabecalhos()
    {
        var request = Request(attempt: 2);

        var result = await Sender(allowPrivate: true).SendAsync(request, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(200, result.StatusCode);
        var received = Assert.Single(_captured);
        Assert.Equal("POST", received.Method);
        Assert.Equal("/hook", received.Path);
        Assert.Equal(Encoding.UTF8.GetString(request.Body), received.Body);
        Assert.StartsWith("application/json", received.ContentType, StringComparison.Ordinal);
        Assert.Equal(request.Signature, received.Headers["X-Webhook-Signature"]);
        Assert.True(WebhookSignature.Verify("segredo-de-teste-com-16-chars", Encoding.UTF8.GetBytes(received.Body), received.Headers["X-Webhook-Signature"]));
        Assert.Equal("document.completed", received.Headers["X-Webhook-Event"]);
        Assert.Equal(request.DeliveryId.ToString("D"), received.Headers["X-Webhook-Delivery"]);
        Assert.Equal("2", received.Headers["X-Webhook-Attempt"]);
        Assert.StartsWith("DocReader-Webhook/", received.Headers["User-Agent"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(201, true, null)]
    [InlineData(204, true, null)]
    [InlineData(400, false, "HTTP_400")]
    [InlineData(404, false, "HTTP_404")]
    [InlineData(500, false, "HTTP_500")]
    [InlineData(503, false, "HTTP_503")]
    public async Task Cada_resposta_vira_sucesso_ou_falha_com_o_codigo_http(int status, bool success, string? code)
    {
        _handler = context => { context.Response.StatusCode = status; return Task.CompletedTask; };

        var result = await Sender(allowPrivate: true).SendAsync(Request(), Ct);

        Assert.Equal(success, result.IsSuccess);
        Assert.Equal(status, result.StatusCode);
        Assert.Null(result.ErrorCode);
        if (code is not null)
        {
            Assert.Equal(code, result.FailureCode);
        }
    }

    [Fact]
    public async Task Redirecionamento_nao_e_seguido_e_conta_como_falha()
    {
        _handler = context =>
        {
            context.Response.StatusCode = 302;
            context.Response.Headers["Location"] = _prefix + "outro-lugar";

            return Task.CompletedTask;
        };

        var result = await Sender(allowPrivate: true).SendAsync(Request(), Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal("HTTP_302", result.FailureCode);
        Assert.Single(_captured);
        Assert.DoesNotContain(_captured, received => received.Path == "/outro-lugar");
    }

    [Fact]
    public async Task Resposta_lenta_estoura_o_tempo_e_vira_timeout()
    {
        _handler = async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            context.Response.StatusCode = 200;
        };

        var result = await Sender(allowPrivate: true, timeout: TimeSpan.FromMilliseconds(300)).SendAsync(Request(), Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal("TIMEOUT", result.ErrorCode);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public async Task Conexao_recusada_vira_connection_failed()
    {
        var closedPort = FreePort();
        var request = Request() with { Url = $"http://127.0.0.1:{closedPort}/hook" };

        var result = await Sender(allowPrivate: true).SendAsync(request, Ct);

        Assert.Equal("CONNECTION_FAILED", result.ErrorCode);
    }

    [Fact]
    public async Task Sem_permissao_o_loopback_e_recusado_na_conexao_e_nada_chega_ao_servidor()
    {
        var result = await Sender(allowPrivate: false).SendAsync(Request(), Ct);

        Assert.Equal("BLOCKED_ADDRESS", result.ErrorCode);
        Assert.Empty(_captured);
    }

    [Fact]
    public async Task Nome_que_resolve_para_loopback_tambem_e_recusado_sem_permissao()
    {
        // The name passes any check on the text; the guard looks at where it resolves to.
        var port = new Uri(_prefix).Port;
        var request = Request() with { Url = $"http://localtest.me:{port}/hook" };

        var result = await Sender(allowPrivate: false).SendAsync(request, Ct);

        Assert.NotEqual(200, result.StatusCode);
        Assert.Empty(_captured);
    }

    [Fact]
    public async Task Cancelamento_do_chamador_nao_e_confundido_com_timeout()
    {
        _handler = async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            context.Response.StatusCode = 200;
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Sender(allowPrivate: true, timeout: TimeSpan.FromSeconds(30)).SendAsync(Request(), cancellation.Token));
    }

    private sealed record CapturedRequest(string Method, string Path, string Body, string? ContentType, Dictionary<string, string> Headers);

    private sealed class FixedClientFactory(SocketsHttpHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
