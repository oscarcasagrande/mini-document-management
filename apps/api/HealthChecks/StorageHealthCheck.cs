using DocReader.Application.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DocReader.Api.HealthChecks;

/// <summary>
/// Readiness of the document volume: the API is only ready when it can actually write an original.
/// </summary>
public sealed class StorageHealthCheck(IFileStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var writable = await storage.IsWritableAsync(cancellationToken).ConfigureAwait(false);

        return writable
            ? HealthCheckResult.Healthy("Document storage is writable.")
            : HealthCheckResult.Unhealthy("Document storage is not writable.");
    }
}
