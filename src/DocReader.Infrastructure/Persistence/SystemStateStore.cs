using DocReader.Application.Abstractions;
using DocReader.Domain.Backup;
using Microsoft.EntityFrameworkCore;

namespace DocReader.Infrastructure.Persistence;

/// <summary>Reads and flips the one <c>system_state</c> row with set-based statements, so nothing is tracked.</summary>
public sealed class SystemStateStore(DocReaderDbContext dbContext) : ISystemStateStore
{
    public async Task<bool> IsReadOnlyAsync(CancellationToken ct) =>
        await dbContext.SystemStates
            .AsNoTracking()
            .Where(state => state.Id == SystemState.SingletonId)
            .Select(state => (bool?)state.IsReadOnly)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? false;

    public async Task SetReadOnlyAsync(bool readOnly, DateTimeOffset now, CancellationToken ct)
    {
        var updated = await dbContext.SystemStates
            .Where(state => state.Id == SystemState.SingletonId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(state => state.IsReadOnly, readOnly)
                    .SetProperty(state => state.UpdatedAt, now),
                ct)
            .ConfigureAwait(false);

        if (updated == 0)
        {
            throw new InvalidOperationException("The system_state row is missing: the database was not migrated.");
        }
    }
}
