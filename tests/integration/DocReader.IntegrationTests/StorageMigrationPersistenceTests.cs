using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DocReader.Application.Abstractions;
using DocReader.Application.Audit;
using DocReader.Application.Errors;
using DocReader.Application.Options;
using DocReader.Application.Storage;
using DocReader.Application.StorageMigrations;
using DocReader.Domain.Documents;
using DocReader.Domain.Storage;
using DocReader.Domain.StorageMigrations;
using DocReader.Infrastructure.Persistence;
using DocReader.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// Moving documents between storage repositories against a real PostgreSQL, with the real storage facade, adapters and
/// encryption: file system to database end to end, a misconfigured cloud target that fails every document without
/// stopping the job, the claim, and cancelling.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class StorageMigrationPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"docreader-it-migration-{Guid.NewGuid():N}");

    private readonly AesGcmSecretProtector _protector =
        new(Options.Create(new SecretsOptions { EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private static void RequireDatabase(PostgresFixture fixture) =>
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

    private static StorageRepositoryStore StoreOf(DocReaderDbContext context) => new(context, new FakeTimeProvider(Now));

    private RepositoryFileStorage StorageOf(DocReaderDbContext context) => new(
        StoreOf(context),
        new StorageAdapterFactory(context, Options.Create(new StorageOptions { RootPath = _storageRoot }), NullLoggerFactory.Instance),
        _protector);

    private StorageMigrationService ServiceOf(DocReaderDbContext context, ILogger<StorageMigrationService>? logger = null) => new(
        new StorageMigrationJobRepository(context),
        StoreOf(context),
        StorageOf(context),
        new FakeTimeProvider(Now.AddDays(1)),
        logger ?? NullLogger<StorageMigrationService>.Instance);

    private async Task<StorageRepository> CreateRepositoryAsync(string code, StorageProvider provider, JsonObject? config)
    {
        await using var write = fixture.CreateContext();
        var service = new StorageRepositoryService(
            StoreOf(write),
            _protector,
            new AuditLogService(new EfAuditLogStore(write), new FakeTimeProvider(Now)),
            new FakeTimeProvider(Now),
            NullLogger<StorageRepositoryService>.Instance);

        return await service.CreateAsync(code, code, provider, config, false, true, Ct);
    }

    /// <summary>Stores real bytes in the repository through the facade, then records a completed document pointing at them.</summary>
    private async Task<(Document Document, byte[] Bytes)> StoreDocumentAsync(Guid repositoryId, DateTimeOffset uploadedAt)
    {
        var id = Guid.CreateVersion7(uploadedAt);
        var bytes = RandomNumberGenerator.GetBytes(64 + Random.Shared.Next(64));

        await using var context = fixture.CreateContext();
        var stored = await StorageOf(context).SaveAsync(repositoryId, new MemoryStream(bytes), new FileMetadata(id, ".png", "image/png", uploadedAt), Ct);

        var document = Document.Accept(
            id,
            $"DOC-20260929-{Random.Shared.Next(1, 999_999):D6}",
            "Foto Do Documento.PNG",
            stored.StorageKey,
            "image/png",
            bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            1,
            UploadChannel.Api,
            null,
            null,
            uploadedAt,
            null,
            null,
            repositoryId);
        document.MarkCompleted(uploadedAt);

        context.Documents.Add(document);
        await context.SaveChangesAsync(Ct);

        return (document, bytes);
    }

    private static async Task<byte[]> ReadAllAsync(RepositoryFileStorage storage, Guid repositoryId, string storageKey)
    {
        await using var stream = await storage.OpenReadAsync(repositoryId, storageKey, Ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct);

        return buffer.ToArray();
    }

    [Fact]
    public async Task Aceite_vinte_documentos_do_filesystem_vao_para_o_banco_e_o_original_continua_na_origem()
    {
        RequireDatabase(fixture);

        var source = await CreateRepositoryAsync("ORIGEM-FS", StorageProvider.FileSystem, new JsonObject { ["directory"] = "origem" });
        var target = await CreateRepositoryAsync("DESTINO-DB", StorageProvider.Database, null);

        var seeded = new List<(Document Document, byte[] Bytes)>();
        for (var index = 0; index < 20; index++)
        {
            seeded.Add(await StoreDocumentAsync(source.Id, Now.AddMinutes(-index)));
        }

        StorageMigrationJob job;
        await using (var write = fixture.CreateContext())
        {
            job = await ServiceOf(write).EnqueueAsync(source.Id, target.Id, null, Ct);
        }

        Assert.Equal(StorageMigrationStatus.Pending, job.Status);

        // The worker's path: claim, then process.
        await using (var work = fixture.CreateContext())
        {
            var claimed = await new StorageMigrationJobRepository(work).ClaimNextPendingAsync(Now.AddDays(1), Ct);
            Assert.Equal(job.Id, claimed!.Id);
            Assert.Equal(StorageMigrationStatus.Running, claimed.Status);

            await ServiceOf(work).ProcessAsync(claimed.Id, Ct);
        }

        await using var read = fixture.CreateContext();
        var finished = await new StorageMigrationJobRepository(read).FindByIdAsync(job.Id, Ct);
        Assert.Equal(StorageMigrationStatus.Completed, finished!.Status);
        Assert.Equal(20, finished.DocumentsMigrated);
        Assert.Equal(0, finished.DocumentsFailed);
        Assert.NotNull(finished.CompletedAt);

        var storage = StorageOf(read);
        foreach (var (original, bytes) in seeded)
        {
            var loaded = await read.Documents
                .Include(document => document.Events)
                .AsNoTracking()
                .SingleAsync(document => document.Id == original.Id, Ct);

            Assert.Equal(target.Id, loaded.StorageRepositoryId);
            Assert.Equal($"blobs/{original.Id:D}", loaded.StorageKey);
            Assert.Equal(DocumentStatus.Completed, loaded.Status);

            // Readable from the new repository, byte for byte...
            Assert.Equal(bytes, await ReadAllAsync(storage, target.Id, loaded.StorageKey));

            // ...and still there, untouched, in the old one.
            Assert.Equal(bytes, await ReadAllAsync(storage, source.Id, original.StorageKey));
            Assert.True(File.Exists(Path.Combine(_storageRoot, "origem", original.StorageKey.Replace('/', Path.DirectorySeparatorChar))));

            var moved = Assert.Single(loaded.Events, entry => entry.EventType == DocumentEventTypes.StorageMigrated);
            Assert.True(StorageMigrationEventDetails.TryParse(moved.Details, out var from, out var to));
            Assert.Equal(source.Id, from);
            Assert.Equal(target.Id, to);
            Assert.Equal(Now.AddDays(1), moved.OccurredAt);
        }

        Assert.Equal(20, await read.DocumentBlobs.CountAsync(Ct));
        Assert.Equal(0, await read.Documents.CountAsync(document => document.StorageRepositoryId == source.Id, Ct));
    }

    [Fact]
    public async Task Destino_s3_mal_configurado_falha_cada_documento_registra_o_erro_e_o_job_termina()
    {
        RequireDatabase(fixture);

        // Nothing listens on port 1: every upload fails with a real SDK exception, as a wrong endpoint would in production.
        var broken = await CreateRepositoryAsync("S3-QUEBRADO", StorageProvider.AwsS3, new JsonObject
        {
            ["bucket"] = "docreader-inexistente",
            ["accessKeyId"] = "AKIAINVALIDKEY000000",
            ["secretAccessKey"] = "invalid-secret-access-key",
            ["region"] = "us-east-1",
            ["serviceUrl"] = "http://127.0.0.1:1"
        });

        var seeded = new List<Document>();
        for (var index = 0; index < 3; index++)
        {
            seeded.Add((await StoreDocumentAsync(StorageRepository.DefaultRepositoryId, Now.AddMinutes(-index))).Document);
        }

        StorageMigrationJob job;
        await using (var write = fixture.CreateContext())
        {
            job = await ServiceOf(write).EnqueueAsync(StorageRepository.DefaultRepositoryId, broken.Id, null, Ct);
        }

        var logger = new ListLogger<StorageMigrationService>();
        await using (var work = fixture.CreateContext())
        {
            await ServiceOf(work, logger).ProcessAsync(job.Id, Ct);
        }

        await using var read = fixture.CreateContext();
        var finished = await new StorageMigrationJobRepository(read).FindByIdAsync(job.Id, Ct);
        Assert.Equal(StorageMigrationStatus.Completed, finished!.Status);
        Assert.Equal(0, finished.DocumentsMigrated);
        Assert.Equal(3, finished.DocumentsFailed);

        foreach (var original in seeded)
        {
            var loaded = await read.Documents.Include(document => document.Events).AsNoTracking().SingleAsync(document => document.Id == original.Id, Ct);
            Assert.Equal(StorageRepository.DefaultRepositoryId, loaded.StorageRepositoryId);
            Assert.Equal(original.StorageKey, loaded.StorageKey);
            Assert.DoesNotContain(loaded.Events, entry => entry.EventType == DocumentEventTypes.StorageMigrated);
        }

        var failures = logger.Entries.Where(entry => entry.Level == LogLevel.Error).ToList();
        Assert.Equal(3, failures.Count);
        Assert.All(failures, failure =>
        {
            // A real failure of the AWS SDK reaching the endpoint, not a settings or construction error of ours.
            Assert.IsType<HttpRequestException>(failure.Exception);
            Assert.Contains("Amazon.Runtime", failure.Exception!.StackTrace, StringComparison.Ordinal);
            Assert.Contains($"errorType={failure.Exception.GetType().Name}", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Foto Do Documento", failure.Message, StringComparison.Ordinal);
        });
        Assert.All(seeded, document => Assert.Contains(failures, failure => failure.Message.Contains(document.Id.ToString("D"), StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Cancelar_um_job_em_execucao_faz_o_worker_parar_e_um_job_encerrado_da_conflito()
    {
        RequireDatabase(fixture);

        var target = await CreateRepositoryAsync("DESTINO-DB", StorageProvider.Database, null);
        var (document, _) = await StoreDocumentAsync(StorageRepository.DefaultRepositoryId, Now);

        StorageMigrationJob job;
        await using (var write = fixture.CreateContext())
        {
            job = await ServiceOf(write).EnqueueAsync(StorageRepository.DefaultRepositoryId, target.Id, null, Ct);
        }

        await using (var claim = fixture.CreateContext())
        {
            Assert.NotNull(await new StorageMigrationJobRepository(claim).ClaimNextPendingAsync(Now, Ct));
        }

        // A caller cancels while the job is RUNNING; the worker notices before its first batch and moves nothing.
        await using (var cancel = fixture.CreateContext())
        {
            var cancelled = await ServiceOf(cancel).CancelAsync(job.Id, Ct);
            Assert.Equal(StorageMigrationStatus.Cancelled, cancelled.Status);
        }

        await using (var work = fixture.CreateContext())
        {
            await ServiceOf(work).ProcessAsync(job.Id, Ct);
        }

        await using var read = fixture.CreateContext();
        Assert.Equal(StorageMigrationStatus.Cancelled, (await new StorageMigrationJobRepository(read).FindByIdAsync(job.Id, Ct))!.Status);
        Assert.Equal(
            StorageRepository.DefaultRepositoryId,
            (await read.Documents.AsNoTracking().SingleAsync(item => item.Id == document.Id, Ct)).StorageRepositoryId);

        var conflict = await Assert.ThrowsAsync<ResourceConflictException>(() => ServiceOf(read).CancelAsync(job.Id, Ct));
        Assert.Equal("STORAGE_MIGRATION_NOT_CANCELLABLE", conflict.ErrorCode);

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => ServiceOf(read).CancelAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Reivindicar_o_proximo_pendente_e_atomico_e_ignora_cancelados()
    {
        RequireDatabase(fixture);

        var target = await CreateRepositoryAsync("DESTINO-DB", StorageProvider.Database, null);

        await using (var empty = fixture.CreateContext())
        {
            Assert.Null(await new StorageMigrationJobRepository(empty).ClaimNextPendingAsync(Now, Ct));
        }

        StorageMigrationJob cancelledJob;
        StorageMigrationJob pendingJob;
        await using (var write = fixture.CreateContext())
        {
            cancelledJob = await ServiceOf(write).EnqueueAsync(StorageRepository.DefaultRepositoryId, target.Id, null, Ct);
        }

        await using (var write = fixture.CreateContext())
        {
            pendingJob = await ServiceOf(write).EnqueueAsync(
                StorageRepository.DefaultRepositoryId,
                target.Id,
                new StorageMigrationFilter("BR_CNH", null, Now.AddDays(-30), Now),
                Ct);
        }

        await using (var cancel = fixture.CreateContext())
        {
            await ServiceOf(cancel).CancelAsync(cancelledJob.Id, Ct);
        }

        await using (var claimContext = fixture.CreateContext())
        {
            var claimed = await new StorageMigrationJobRepository(claimContext).ClaimNextPendingAsync(Now.AddMinutes(1), Ct);
            Assert.Equal(pendingJob.Id, claimed!.Id);
            Assert.Equal(StorageMigrationStatus.Running, claimed.Status);
            Assert.Equal("BR_CNH", claimed.FilterDocumentType);
            Assert.Equal(Now.AddDays(-30), claimed.FilterUploadedFrom);
        }

        await using var read = fixture.CreateContext();
        Assert.Null(await new StorageMigrationJobRepository(read).ClaimNextPendingAsync(Now.AddMinutes(2), Ct));
    }
}
