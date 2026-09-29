using DocReader.Application.Abstractions;
using DocReader.Application.Backup;
using DocReader.Application.Errors;
using DocReader.Domain.Backup;
using DocReader.Infrastructure.Backup;

namespace DocReader.UnitTests.Fakes;

public sealed class InMemoryBackupJobStore : IBackupJobRepository
{
    public List<BackupJob> Items { get; } = [];

    public Task AddAsync(BackupJob job, CancellationToken ct)
    {
        Items.Add(job);
        return Task.CompletedTask;
    }

    public Task<BackupJob?> FindByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(job => job.Id == id));

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<BackupJob?> ClaimNextPendingAsync(DateTimeOffset now, CancellationToken ct)
    {
        var next = Items.Where(job => job.Status == BackupJobStatus.Pending).OrderBy(job => job.RequestedAt).FirstOrDefault();
        next?.MarkRunning(now);
        return Task.FromResult(next);
    }
}

public sealed class InMemoryRestoreJobStore : IRestoreJobRepository
{
    public List<RestoreJob> Items { get; } = [];

    public int ForgetCalls { get; private set; }

    public Task AddAsync(RestoreJob job, CancellationToken ct)
    {
        if (Items.Any(item => item.Status is RestoreJobStatus.Pending or RestoreJobStatus.Running))
        {
            throw new ResourceConflictException("RESTORE_ALREADY_IN_PROGRESS", "Another restore is pending or running.");
        }

        Items.Add(job);
        return Task.CompletedTask;
    }

    public Task<RestoreJob?> FindByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(job => job.Id == id));

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<bool> HasActiveAsync(CancellationToken ct) =>
        Task.FromResult(Items.Any(job => job.Status is RestoreJobStatus.Pending or RestoreJobStatus.Running));

    public Task<IReadOnlyList<Guid>> ListRunningAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. Items.Where(job => job.Status == RestoreJobStatus.Running).Select(job => job.Id)]);

    public Task<RestoreJob?> ClaimNextPendingAsync(DateTimeOffset now, CancellationToken ct)
    {
        var next = Items.Where(job => job.Status == RestoreJobStatus.Pending).OrderBy(job => job.RequestedAt).FirstOrDefault();
        next?.MarkRunning(now);
        return Task.FromResult(next);
    }

    public void ForgetLoadedState() => ForgetCalls++;
}

public sealed class InMemorySystemState : ISystemStateStore
{
    public bool IsReadOnly { get; set; }

    public List<bool> Changes { get; } = [];

    public Task<bool> IsReadOnlyAsync(CancellationToken ct) => Task.FromResult(IsReadOnly);

    public Task SetReadOnlyAsync(bool readOnly, DateTimeOffset now, CancellationToken ct)
    {
        IsReadOnly = readOnly;
        Changes.Add(readOnly);
        return Task.CompletedTask;
    }
}

/// <summary>Records what a restore would replay and the state of the read-only gate at that moment; can be told to fail.</summary>
public sealed class RecordingDatabaseRestorer(ISystemStateStore state) : IDatabaseRestorer
{
    public string? RestoredSql { get; private set; }

    public bool? ReadOnlyDuringRestore { get; private set; }

    public Exception? FailWith { get; set; }

    /// <summary>Document ids the restored database "has"; null means every one asked for.</summary>
    public HashSet<Guid>? ExistingDocuments { get; set; }

    public async Task RestoreAsync(string databaseDumpPath, CancellationToken ct)
    {
        ReadOnlyDuringRestore = await state.IsReadOnlyAsync(ct);

        if (FailWith is not null)
        {
            throw FailWith;
        }

        RestoredSql = await File.ReadAllTextAsync(databaseDumpPath, ct);
    }

    public Task<int> CountMissingDocumentsAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken ct) =>
        Task.FromResult(ExistingDocuments is null ? 0 : documentIds.Count(id => !ExistingDocuments.Contains(id)));
}

/// <summary>Writes a fixed script as the "dump" and returns a fixed manifest, so the real archive builder runs without PostgreSQL.</summary>
public sealed class ScriptDumper(string sql, IReadOnlyList<BackupManifestEntry> manifest) : IDatabaseDumper
{
    public async Task<IReadOnlyList<BackupManifestEntry>> DumpAsync(string outputPath, CancellationToken ct)
    {
        await File.WriteAllTextAsync(outputPath, sql, ct);
        return manifest;
    }
}
