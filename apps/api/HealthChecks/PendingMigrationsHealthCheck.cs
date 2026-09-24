using DocReader.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DocReader.Api.HealthChecks;

/// <summary>
/// Fails readiness while migrations are pending, so a container whose schema was never migrated is
/// never routed traffic just because the process is up.
/// </summary>
public sealed class PendingMigrationsHealthCheck(DocReaderDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pending = await dbContext.Database
                .GetPendingMigrationsAsync(cancellationToken)
                .ConfigureAwait(false);

            var pendingCount = pending.Count();

            return pendingCount == 0
                ? HealthCheckResult.Healthy("Database schema is up to date.")
                : HealthCheckResult.Unhealthy($"{pendingCount} migration(s) are pending.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Could not read the migration history.", exception);
        }
    }
}
