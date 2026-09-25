using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using Microsoft.Extensions.Logging;

namespace DocReader.Infrastructure.Ocr;

/// <summary>
/// <see cref="IDocumentOcrProvider"/> that talks to the internal ocr-service (RF-008): the original
/// file goes out once per page, with the page number, and blocks with confidence and coordinates
/// come back. The service rasterizes PDF pages itself, so this class knows nothing about imaging.
///
/// Sending one page per call is what lets the worker renew its lock between pages and gives every
/// page its own timeout. The cost is re-sending the file, which stays on the internal network.
///
/// Failures are classified here, because only this class knows what the service meant: a service
/// that is down, busy, still loading or too slow is transient; a file the service cannot decode is not.
/// Neither the text of the document nor of the service's error ever reaches an exception message.
/// </summary>
public sealed class PaddleOcrServiceProvider(HttpClient httpClient, ILogger<PaddleOcrServiceProvider> logger)
    : IDocumentOcrProvider
{
    public const string PagePath = "v1/ocr/page";
    public const string CorrelationHeader = "X-Correlation-Id";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ProviderName => "paddleocr";

    public async Task<OcrResult> AnalyzeAsync(
        DocumentContent document,
        OcrOptions options,
        IOcrProgress? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var pages = new List<OcrPage>(document.PageCount);
        var rawPages = new List<PageAnalysisDto>(document.PageCount);
        var modelVersion = "unknown";

        // The multipart form disposes the content it wraps, so each page gets its own copy of the bytes;
        // handing the stream over would close it after the first page.
        using var buffer = new MemoryStream();
        await document.Content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var original = buffer.ToArray();

        for (var pageNumber = 1; pageNumber <= document.PageCount; pageNumber++)
        {
            ct.ThrowIfCancellationRequested();

            var started = Stopwatch.GetTimestamp();
            var analysis = await AnalyzePageAsync(document.MimeType, original, pageNumber, options, ct).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started);

            modelVersion = analysis.ModelVersion;
            rawPages.Add(analysis);

            var blocks = analysis.Blocks
                .Select(block => new OcrBlock(
                    block.Text,
                    block.Confidence,
                    (block.BoundingBox ?? []).Select(coordinate => (decimal)coordinate).ToArray()))
                .ToArray();

            pages.Add(new OcrPage(pageNumber, string.Join('\n', blocks.Select(block => block.Text)), blocks));

            if (progress is not null)
            {
                await progress
                    .OnPageCompletedAsync(new OcrPageProgress(pageNumber, document.PageCount, elapsed, blocks.Length), ct)
                    .ConfigureAwait(false);
            }
        }

        return new OcrResult(ProviderName, modelVersion, pages, BuildRawResult(rawPages));
    }

    private async Task<PageAnalysisDto> AnalyzePageAsync(
        string mimeType,
        byte[] original,
        int pageNumber,
        OcrOptions options,
        CancellationToken ct)
    {
        // The budget covers this page only, so a long document is not punished for its length.
        using var pageBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pageBudget.CancelAfter(options.Timeout);

        try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(original);
            file.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            // The name is a placeholder on purpose: the original file name is metadata, not evidence,
            // and it never leaves the API.
            form.Add(file, "file", "original");
            form.Add(new StringContent(pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)), "page");

            using var request = new HttpRequestMessage(HttpMethod.Post, PagePath) { Content = form };
            if (!string.IsNullOrEmpty(options.CorrelationId))
            {
                request.Headers.TryAddWithoutValidation(CorrelationHeader, options.CorrelationId);
            }

            using var response = await httpClient.SendAsync(request, pageBudget.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(pageBudget.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw Classify(response.StatusCode, body, pageNumber);
            }

            return Parse(body);
        }
        catch (OcrProviderException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "OCR page exceeded the worker budget. page={Page} timeoutSeconds={Timeout}",
                pageNumber,
                options.Timeout.TotalSeconds);

            throw new OcrProviderException(
                "OCR_PAGE_TIMEOUT",
                $"The OCR service did not finish page {pageNumber} within {options.Timeout.TotalSeconds:0} seconds.",
                isTransient: true);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                "OCR service unreachable. page={Page} statusCode={StatusCode} errorType={ErrorType}",
                pageNumber,
                exception.StatusCode,
                exception.InnerException?.GetType().Name ?? exception.GetType().Name);

            throw new OcrProviderException(
                "OCR_UNAVAILABLE",
                "The OCR service could not be reached. The document will be retried automatically.",
                isTransient: true,
                exception);
        }
        catch (JsonException exception)
        {
            throw new OcrProviderException(
                "OCR_INVALID_RESPONSE",
                "The OCR service answered something the worker could not understand.",
                isTransient: true,
                exception);
        }
    }

    /// <summary>Maps a refusal of the service to an exception that says whether a retry can help.</summary>
    private OcrProviderException Classify(HttpStatusCode status, string body, int pageNumber)
    {
        var serviceCode = TryReadErrorCode(body);

        logger.LogWarning(
            "OCR service refused the page. page={Page} statusCode={StatusCode} serviceErrorCode={ServiceErrorCode}",
            pageNumber,
            (int)status,
            serviceCode ?? "none");

        return status switch
        {
            // Not loaded yet, or every slot is busy: the situation changes by itself.
            HttpStatusCode.ServiceUnavailable => new OcrProviderException(
                "OCR_UNAVAILABLE",
                "The OCR service is busy or still loading its models. The document will be retried automatically.",
                isTransient: true),

            HttpStatusCode.GatewayTimeout => new OcrProviderException(
                "OCR_PAGE_TIMEOUT",
                $"The OCR service took too long on page {pageNumber}.",
                isTransient: true),

            // The file itself is the problem, and sending it again will not change that.
            HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity or HttpStatusCode.RequestEntityTooLarge =>
                new OcrProviderException(
                    serviceCode ?? "OCR_INPUT_REJECTED",
                    "The OCR service could not read this file. Check that it is a valid, readable PDF, PNG, JPEG or TIFF.",
                    isTransient: false),

            _ when (int)status >= 500 => new OcrProviderException(
                "OCR_SERVICE_ERROR",
                "The OCR service failed while reading the page. The document will be retried automatically.",
                isTransient: true),

            _ => new OcrProviderException(
                "OCR_REQUEST_REJECTED",
                "The OCR service refused the request.",
                isTransient: false)
        };
    }

    private static string? TryReadErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PageAnalysisDto Parse(string body)
    {
        var page = JsonSerializer.Deserialize<PageAnalysisDto>(body, Json)
            ?? throw new JsonException("Empty OCR answer.");

        return page with { Blocks = page.Blocks ?? [] };
    }

    /// <summary>
    /// Keeps each page's provider payload as it came, wrapped with the page number, so RF-008 holds:
    /// the raw result is preserved, not reinterpreted.
    /// </summary>
    private static string BuildRawResult(IReadOnlyList<PageAnalysisDto> pages)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("pages");

            foreach (var page in pages)
            {
                writer.WriteStartObject();
                writer.WriteNumber("page", page.Page);
                writer.WriteNumber("imageWidth", page.ImageWidth);
                writer.WriteNumber("imageHeight", page.ImageHeight);
                writer.WriteNumber("durationMs", page.DurationMs);
                writer.WritePropertyName("raw");
                if (page.Raw.ValueKind == JsonValueKind.Undefined)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    page.Raw.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed record PageAnalysisDto(
        int Page,
        int PageCount,
        int ImageWidth,
        int ImageHeight,
        string ModelVersion,
        int DurationMs,
        List<BlockDto> Blocks,
        JsonElement Raw);

    private sealed record BlockDto(string Text, decimal? Confidence, List<double> BoundingBox);
}
