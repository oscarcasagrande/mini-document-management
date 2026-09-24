using DocReader.Api.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace DocReader.Api.HealthChecks;

/// <summary>
/// Readiness of the internal OCR service (RF-016). In stage 1 the service is a stub that answers
/// <c>/health</c>; the probe already lives here so stage 2 only has to make it do real work.
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
