using System.Net;
using System.Text;
using System.Text.Json;
using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using DocReader.Infrastructure.Ocr;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DocReader.UnitTests.Infrastructure;

/// <summary>
/// The HTTP provider against a scripted handler: what it sends, what it makes of the answers, and
/// above all how it classifies a failure as transient or definitive.
/// </summary>
public sealed class PaddleOcrServiceProviderTests
{
    private static readonly byte[] FileBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03];

    private static readonly OcrOptions Options = new(["pt"], DetectLayout: false, TimeSpan.FromSeconds(5), "corr-1234567890");

    private static string PageJson(int page, int pageCount = 1, string text = "111.444.777-35") =>
        JsonSerializer.Serialize(new
        {
            page,
            pageCount,
            imageWidth = 1100,
            imageHeight = 700,
            provider = "paddleocr",
            modelVersion = "PP-OCRv5 test",
            durationMs = 7000,
            blocks = new[] { new { text, confidence = 0.999, boundingBox = new[] { 1.0, 2.0, 30.0, 2.0, 30.0, 12.0, 1.0, 12.0 } } },
            raw = new { rec_texts = new[] { text } }
        });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Problem(HttpStatusCode status, string errorCode) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { errorCode, detail = "SEGREDO 111.444.777-35 no corpo do erro" }),
                Encoding.UTF8,
                "application/problem+json")
        };

    private static DocumentContent Document(int pageCount = 1) =>
        new(Guid.NewGuid(), "image/png", pageCount, new MemoryStream(FileBytes));

    private static PaddleOcrServiceProvider ProviderFor(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(
            new HttpClient(new StubHandler(handler))
            {
                BaseAddress = new Uri("http://ocr-service:8000/"),
                Timeout = Timeout.InfiniteTimeSpan
            },
            NullLogger<PaddleOcrServiceProvider>.Instance);

    [Fact]
    public async Task Le_uma_pagina_e_devolve_texto_blocos_e_versao_do_modelo()
    {
        var provider = ProviderFor((_, _) => Task.FromResult(Json(HttpStatusCode.OK, PageJson(1))));

        var result = await provider.AnalyzeAsync(Document(), Options, null, TestContext.Current.CancellationToken);

        Assert.Equal("paddleocr", result.ProviderName);
        Assert.Equal("PP-OCRv5 test", result.ModelVersion);

        var page = Assert.Single(result.Pages);
        Assert.Equal(1, page.PageNumber);
        Assert.Equal("111.444.777-35", page.Text);

        var block = Assert.Single(page.Blocks);
        Assert.Equal(0.999m, block.Confidence);
        Assert.Equal(8, block.BoundingBox.Count);
    }

    [Fact]
    public async Task Preserva_o_resultado_bruto_de_cada_pagina_como_veio_do_servico()
    {
        var provider = ProviderFor((_, _) => Task.FromResult(Json(HttpStatusCode.OK, PageJson(1))));

        var result = await provider.AnalyzeAsync(Document(), Options, null, TestContext.Current.CancellationToken);

        using var raw = JsonDocument.Parse(result.RawResult);
        var page = raw.RootElement.GetProperty("pages")[0];

        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(1100, page.GetProperty("imageWidth").GetInt32());
        Assert.Equal("111.444.777-35", page.GetProperty("raw").GetProperty("rec_texts")[0].GetString());
    }

    [Fact]
    public async Task Manda_uma_pagina_por_chamada_com_o_arquivo_inteiro_e_o_correlation_id()
    {
        var seen = new List<(string Path, string Body, string? Correlation)>();

        var provider = ProviderFor(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            seen.Add((request.RequestUri!.AbsolutePath, body, request.Headers.TryGetValues("X-Correlation-Id", out var values) ? values.Single() : null));

            var page = seen.Count;
            return Json(HttpStatusCode.OK, PageJson(page, 3));
        });

        await provider.AnalyzeAsync(Document(pageCount: 3), Options, null, TestContext.Current.CancellationToken);

        Assert.Equal(3, seen.Count);
        Assert.All(seen, call =>
        {
            Assert.Equal("/v1/ocr/page", call.Path);
            Assert.Equal("corr-1234567890", call.Correlation);
            // The whole file goes out every time: the stream has to be rewound between pages.
            Assert.Contains("PNG", call.Body);
            // The original file name is metadata and never leaves the API.
            Assert.DoesNotContain("filename=cartao", call.Body);
        });

        Assert.Contains("\r\n1\r\n", seen[0].Body);
        Assert.Contains("\r\n2\r\n", seen[1].Body);
        Assert.Contains("\r\n3\r\n", seen[2].Body);
    }

    [Fact]
    public async Task Avisa_o_observador_depois_de_cada_pagina_e_antes_de_pedir_a_proxima()
    {
        var events = new List<string>();

        var provider = ProviderFor((_, _) =>
        {
            events.Add($"request {events.Count(entry => entry.StartsWith("request")) + 1}");
            return Task.FromResult(Json(HttpStatusCode.OK, PageJson(events.Count(entry => entry.StartsWith("request")), 2)));
        });

        var observer = new RecordingProgress(events);

        await provider.AnalyzeAsync(Document(pageCount: 2), Options, observer, TestContext.Current.CancellationToken);

        Assert.Equal(["request 1", "page 1/2", "request 2", "page 2/2"], events);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "OCR_UNAVAILABLE", true)]
    [InlineData(HttpStatusCode.GatewayTimeout, "OCR_PAGE_TIMEOUT", true)]
    [InlineData(HttpStatusCode.InternalServerError, "OCR_SERVICE_ERROR", true)]
    [InlineData(HttpStatusCode.BadGateway, "OCR_SERVICE_ERROR", true)]
    [InlineData(HttpStatusCode.BadRequest, "OCR_REQUEST_REJECTED", false)]
    public async Task Classifica_a_recusa_do_servico_como_transitoria_ou_definitiva(
        HttpStatusCode status,
        string expectedCode,
        bool expectedTransient)
    {
        var provider = ProviderFor((_, _) => Task.FromResult(Problem(status, "SERVICE_CODE")));

        var error = await Assert.ThrowsAsync<OcrProviderException>(() =>
            provider.AnalyzeAsync(Document(), Options, null, TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedTransient, error.IsTransient);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "UNREADABLE_FILE")]
    [InlineData(HttpStatusCode.UnsupportedMediaType, "UNSUPPORTED_FORMAT")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "FILE_TOO_LARGE")]
    public async Task Arquivo_que_o_servico_nao_consegue_ler_e_falha_definitiva_com_o_codigo_do_servico(
        HttpStatusCode status,
        string serviceCode)
    {
        var provider = ProviderFor((_, _) => Task.FromResult(Problem(status, serviceCode)));

        var error = await Assert.ThrowsAsync<OcrProviderException>(() =>
            provider.AnalyzeAsync(Document(), Options, null, TestContext.Current.CancellationToken));

        Assert.Equal(serviceCode, error.Code);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task Nunca_copia_o_corpo_do_erro_para_a_mensagem_da_excecao()
    {
        var provider = ProviderFor((_, _) => Task.FromResult(Problem(HttpStatusCode.UnprocessableEntity, "UNREADABLE_FILE")));

        var error = await Assert.ThrowsAsync<OcrProviderException>(() =>
            provider.AnalyzeAsync(Document(), Options, null, TestContext.Current.CancellationToken));

        Assert.DoesNotContain("SEGREDO", error.Message);
        Assert.DoesNotContain("111.444.777-35", error.Message);
    }

    [Fact]
    public async Task Servico_fora_do_ar_e_falha_transitoria()
    {
        var provider = ProviderFor((_, _) => throw new HttpRequestException("Connection refused"));

        var error = await Assert.ThrowsAsync<OcrProviderException>(() =>
            provider.AnalyzeAsync(Document(), Options, null, TestContext.Current.CancellationToken));

        Assert.Equal("OCR_UNAVAILABLE", error.Code);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task Pagina_que_estoura_o_tempo_e_falha_transitoria_com_codigo_proprio()
    {
        var provider = ProviderFor(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json(HttpStatusCode.OK, PageJson(1));
        });

        var error = await Assert.ThrowsAsync<OcrProviderException>(() =>
            provider.AnalyzeAsync(
                Document(),
                Options with { Timeout = TimeSpan.FromMilliseconds(50) },
                null,
                TestContext.Current.CancellationToken));

        Assert.Equal("OCR_PAGE_TIMEOUT", error.Code);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task Cancelamento_de_quem_chamou_nao_vira_falha_de_ocr()
    {
        using var cancellation = new CancellationTokenSource();

        var provider = ProviderFor(async (_, ct) =>
        {
            await cancellation.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return Json(HttpStatusCode.OK, PageJson(1));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.AnalyzeAsync(Document(), Options, null, cancellation.Token));
    }

    [Fact]
    public async Task Resposta_que_nao_e_json_valido_e_falha_transitoria()
    {
        var provider = ProviderFor((_, _) => Task.FromResult(Json(HttpStatusCode.OK, "<html>not json</html>")));

        var error = await Assert.ThrowsAsync<OcrProviderException>(() =>
            provider.AnalyzeAsync(Document(), Options, null, TestContext.Current.CancellationToken));

        Assert.Equal("OCR_INVALID_RESPONSE", error.Code);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task Falha_na_segunda_pagina_nao_devolve_resultado_parcial()
    {
        var calls = 0;
        var provider = ProviderFor((_, _) => Task.FromResult(++calls == 1
            ? Json(HttpStatusCode.OK, PageJson(1, 2))
            : Problem(HttpStatusCode.ServiceUnavailable, "OCR_BUSY")));

        await Assert.ThrowsAsync<OcrProviderException>(() =>
            provider.AnalyzeAsync(Document(pageCount: 2), Options, null, TestContext.Current.CancellationToken));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class RecordingProgress(List<string> events) : IOcrProgress
    {
        public Task OnPageCompletedAsync(OcrPageProgress page, CancellationToken ct)
        {
            events.Add($"page {page.PageNumber}/{page.PageCount}");
            return Task.CompletedTask;
        }
    }
}
