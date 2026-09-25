using DocReader.Api.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DocReader.Api.HealthChecks;

/// <summary>
/// Readiness of the internal OCR service (RF-016). The service answers <c>/health</c> with 503 until its
/// models are loaded and warm, so a healthy answer means it can read a page.
/// </summary>
public sealed class OcrServiceHealthCheck(
    IHttpClientFactory httpClientFactory,
    IOptions<OcrServiceOptions> options) : IHealthCheck
{
    public const string HttpClientName = "ocr-health";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client
                .GetAsync(settings.HealthPath, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("OCR service answered its health probe.")
                : HealthCheckResult.Unhealthy(
                    $"OCR service answered the health probe with status {(int)response.StatusCode}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("OCR service is unreachable.", exception);
        }
    }
}
