using DocReader.Application.Abstractions;
using DocReader.Domain.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocReader.Infrastructure.Persistence;

/// <summary>
/// Reads and expires <c>Idempotency-Key</c> records. An expired record is deleted on the way, so
/// the key becomes reusable without a background job.
/// </summary>
public sealed class PostgresIdempotencyStore(
    DocReaderDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<PostgresIdempotencyStore> logger) : IIdempotencyStore
{
    public async Task<IdempotencyRecord?> FindLiveAsync(string key, CancellationToken ct)
    {
        var record = await dbContext.IdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Key == key, ct)
            .ConfigureAwait(false);

        if (record is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        if (!record.IsExpired(now))
        {
            return record;
        }

        var removed = await dbContext.IdempotencyKeys
            .Where(candidate => candidate.Key == key && candidate.ExpiresAt <= now)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        if (removed > 0)
        {
            logger.LogInformation("Expired Idempotency-Key discarded. documentId={DocumentId}", record.DocumentId);
        }

        return null;
    }
}
