using System.Security.Cryptography;
using DocReader.Application.Abstractions;
using DocReader.Application.Backup;
using DocReader.Application.Errors;
using DocReader.Application.Options;
using DocReader.Application.Storage;
using DocReader.Domain.Backup;
using DocReader.Domain.Documents;
using DocReader.Domain.Storage;
using DocReader.Infrastructure.Backup;
using DocReader.Infrastructure.Persistence;
using DocReader.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// Backup and restore against a real PostgreSQL with the real <c>pg_dump</c> and <c>psql</c>. Every database touched here
/// is disposable: the source is the fixture's <c>docreader_it_*</c> database and every restore target is another
/// <c>docreader_it_*</c> database created by the test and dropped when it ends. A guard refuses to restore into anything
/// else. Skipped without a database or without the client tools; to run them:
///
///   docker build -t docreader-sdk-pgclient - &lt; (a Dockerfile FROM mcr.microsoft.com/dotnet/sdk:10.0 that installs
///     postgresql-client-17 from apt.postgresql.org, as deploy/docker/Dockerfile.worker does)
///   docker run --rm --network docreader_internal -v "$PWD:/src" -w /src
///     -e "DOCREADER_TEST_CONNECTION=Host=postgres;Port=5432;Database=postgres;Username=docreader;Password=docreader"
///     docreader-sdk-pgclient dotnet test tests/integration/DocReader.IntegrationTests
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class BackupRestoreTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DisposablePrefix = "docreader_it_";

    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "docreader-it-backup", Guid.NewGuid().ToString("N"));
    private readonly List<string> _createdDatabases = [];
    private readonly FakeTimeProvider _time = new(Now);
    private readonly SecretsOptions _secrets = new() { EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string SourceStorage => Path.Combine(_root, "source-storage");

    private string TargetStorage => Path.Combine(_root, "target-storage");

    private string WorkDirectory => Path.Combine(_root, "work");

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();

        foreach (var name in _createdDatabases)
        {
            try
            {
                await using var admin = new NpgsqlConnection(AdminConnectionString());
                await admin.OpenAsync();
                await using var drop = admin.CreateCommand();
                drop.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE);";
                await drop.ExecuteNonQueryAsync();
            }
            catch
            {
                // Disposable test database: left behind is not worth failing the suite.
            }
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written.
        }
    }

    // ---- the acceptance scenario ------------------------------------------------------------------------------

    [Fact]
    public async Task Backup_de_10_documentos_restaurado_num_banco_vazio_traz_de_volta_as_linhas_e_os_arquivos()
    {
        await RequireDatabaseAndToolsAsync();

        // Source: 10 documents, 7 in the file system repository and 3 in a database repository, each with its own bytes.
        var databaseRepository = StorageRepository.Create(Guid.CreateVersion7(Now), "ARQUIVO-DB", "Arquivo no banco", StorageProvider.Database, null, false, true, Now);
        await using (var write = fixture.CreateContext())
        {
            await new StorageRepositoryStore(write, _time).AddAsync(databaseRepository, makeDefault: false, Ct);
        }

        var originals = new Dictionary<Guid, byte[]>();
        await using (var write = fixture.CreateContext())
        {
            var storage = Facade(write, SourceStorage);

            for (var index = 0; index < 10; index++)
            {
                var uploadedAt = Now.AddMinutes(-index);
                var id = Guid.CreateVersion7(uploadedAt);
                var bytes = RandomNumberGenerator.GetBytes(2048 + index);
                var repositoryId = index < 7 ? StorageRepository.DefaultRepositoryId : databaseRepository.Id;

                StoredFile stored;
                await using (var content = new MemoryStream(bytes))
                {
                    stored = await storage.SaveAsync(repositoryId, content, new FileMetadata(id, ".pdf", "application/pdf", uploadedAt), Ct);
                }

                var document = Document.Accept(
                    id, $"DOC-20260929-{index + 1:D6}", $"documento-{index}.pdf", stored.StorageKey, "application/pdf", bytes.Length,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)), 1, UploadChannel.Api, $"REF-{index}", null, uploadedAt,
                    storageRepositoryId: repositoryId);
                document.MarkCompleted(uploadedAt);
                write.Documents.Add(document);
                originals[id] = bytes;
            }

            await write.SaveChangesAsync(Ct);
        }

        // Backup, saved in the (file system) default repository of the source.
        BackupJob backup;
        await using (var enqueue = fixture.CreateContext())
        {
            backup = await BackupServiceFor(enqueue, fixture.ConnectionString!, SourceStorage).EnqueueAsync(null, Ct);
        }

        await using (var process = fixture.CreateContext())
        {
            await BackupServiceFor(process, fixture.ConnectionString!, SourceStorage).ProcessAsync(backup.Id, Ct);
        }

        byte[] archive;
        await using (var read = fixture.CreateContext())
        {
            var completed = await new BackupJobRepository(read).FindByIdAsync(backup.Id, Ct);
            Assert.Equal(BackupJobStatus.Completed, completed!.Status);
            Assert.Null(completed.ErrorMessage);
            Assert.Equal(10, completed.DocumentCount);
            Assert.Equal(10, completed.FilesArchived);
            Assert.Equal($"backup-{backup.Id:D}.tar.gz", completed.FileName);
            Assert.Equal(StorageRepository.DefaultRepositoryId, completed.StorageRepositoryId);

            await using var stored = await Facade(read, SourceStorage).OpenReadAsync(completed.StorageRepositoryId, completed.StorageKey!, Ct);
            using var buffer = new MemoryStream();
            await stored.CopyToAsync(buffer, Ct);
            archive = buffer.ToArray();

            Assert.Equal(completed.SizeBytes, archive.Length);
            Assert.Equal(completed.ChecksumSha256, Convert.ToHexStringLower(SHA256.HashData(archive)));
        }

        // Target: a brand new, migrated database with nothing of the source, plus one document of its own that the
        // restore must replace, and an empty storage root (the volume was lost too).
        var target = await CreateMigratedDatabaseAsync();
        var strayId = Guid.CreateVersion7(Now.AddDays(-3));
        await using (var write = ContextFor(target))
        {
            var stray = Document.Accept(
                strayId, "DOC-20260926-999999", "outro.pdf", $"documents/2026/09/26/{strayId:D}/original.pdf", "application/pdf", 10,
                new string('f', 64), 1, UploadChannel.Api, null, null, Now.AddDays(-3));
            write.Documents.Add(stray);
            await write.SaveChangesAsync(Ct);
        }

        RestoreJob restore;
        await using (var enqueue = ContextFor(target))
        {
            await using var upload = new MemoryStream(archive);
            restore = await RestoreServiceFor(enqueue, target, TargetStorage).EnqueueAsync(upload, Ct);
        }

        Assert.Equal(RestoreJobStatus.Pending, restore.Status);
        Assert.Equal(10, restore.DocumentCount);

        await using (var process = ContextFor(target))
        {
            await RestoreServiceFor(process, target, TargetStorage).ProcessAsync(restore.Id, Ct);
        }

        // The rows came back, exactly those, and the target's own document is gone.
        await using (var read = ContextFor(target))
        {
            var job = await new RestoreJobRepository(read).FindByIdAsync(restore.Id, Ct);
            Assert.Null(job!.ErrorMessage);
            Assert.Equal(RestoreJobStatus.Completed, job.Status);
            Assert.Equal(10, job.FilesRestored);

            var documents = await read.Documents.AsNoTracking().ToListAsync(Ct);
            Assert.Equal(originals.Keys.Order(), documents.Select(document => document.Id).Order());
            Assert.DoesNotContain(documents, document => document.Id == strayId);
            Assert.All(documents, document => Assert.Equal(DocumentStatus.Completed, document.Status));
            Assert.Contains(await read.StorageRepositories.AsNoTracking().ToListAsync(Ct), repository => repository.Id == databaseRepository.Id);

            // And the content: every file read back through the storage of the target has the original bytes.
            var storage = Facade(read, TargetStorage);
            foreach (var document in documents)
            {
                await using var content = await storage.OpenReadAsync(document.StorageRepositoryId, document.StorageKey, Ct);
                using var buffer = new MemoryStream();
                await content.CopyToAsync(buffer, Ct);

                Assert.Equal(originals[document.Id], buffer.ToArray());
                Assert.Equal(document.Sha256, Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray())));
            }

            // Operational state survives: the gate is down, the job row was not replaced by the dump, and the uploaded
            // archive was removed from the target's storage.
            Assert.False(await new SystemStateStore(read).IsReadOnlyAsync(Ct));
            Assert.Empty(await read.BackupJobs.AsNoTracking().ToListAsync(Ct));
            await Assert.ThrowsAsync<FileNotFoundException>(() => storage.OpenReadAsync(job.ArchiveStorageRepositoryId, job.ArchiveStorageKey, Ct));
        }

        // The file system files really are on the target's disk, where the key says.
        foreach (var document in originals.Keys.Take(7))
        {
            Assert.Single(Directory.EnumerateFiles(TargetStorage, "original.pdf", SearchOption.AllDirectories), path => path.Contains(document.ToString("D"), StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Restore_que_falha_no_meio_do_sql_desfaz_tudo_e_o_banco_fica_como_estava()
    {
        await RequireDatabaseAndToolsAsync();

        var target = await CreateMigratedDatabaseAsync();
        var kept = new List<Guid>();
        await using (var write = ContextFor(target))
        {
            for (var index = 0; index < 3; index++)
            {
                var id = Guid.CreateVersion7(Now.AddMinutes(-index));
                write.Documents.Add(Document.Accept(
                    id, $"DOC-20260929-{index + 1:D6}", "doc.pdf", $"documents/2026/09/29/{id:D}/original.pdf", "application/pdf", 10,
                    new string('a', 64), 1, UploadChannel.Api, null, null, Now.AddMinutes(-index)));
                kept.Add(id);
            }

            await write.SaveChangesAsync(Ct);
        }

        // A validly signed archive whose SQL destroys data, creates a table, and then fails.
        var archive = await BuildArchiveAsync(
            """
            TRUNCATE TABLE documents CASCADE;
            CREATE TABLE restore_must_roll_back (id integer);
            SELECT 1 / 0;
            """);

        RestoreJob restore;
        await using (var enqueue = ContextFor(target))
        {
            await using var upload = new MemoryStream(archive);
            restore = await RestoreServiceFor(enqueue, target, TargetStorage).EnqueueAsync(upload, Ct);
        }

        await using (var process = ContextFor(target))
        {
            await RestoreServiceFor(process, target, TargetStorage).ProcessAsync(restore.Id, Ct);
        }

        await using var read = ContextFor(target);
        var job = await new RestoreJobRepository(read).FindByIdAsync(restore.Id, Ct);
        Assert.Equal(RestoreJobStatus.Failed, job!.Status);
        Assert.Contains("psql exited with code 3", job.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("division by zero", job.ErrorMessage, StringComparison.Ordinal);

        Assert.Equal(kept.Order(), (await read.Documents.AsNoTracking().Select(document => document.Id).ToListAsync(Ct)).Order());
        var createdTable = await read.Database
            .SqlQuery<string?>($"SELECT to_regclass('public.restore_must_roll_back')::text AS \"Value\"")
            .SingleAsync(Ct);
        Assert.Null(createdTable);
        Assert.False(await new SystemStateStore(read).IsReadOnlyAsync(Ct));
    }

    [Fact]
    public async Task Restore_cujo_manifesto_cita_documento_ausente_do_dump_falha_apos_o_commit()
    {
        await RequireDatabaseAndToolsAsync();

        var target = await CreateMigratedDatabaseAsync();
        var missing = new BackupManifestEntry(Guid.CreateVersion7(Now), StorageRepository.DefaultRepositoryId, "documents/x", StorageProvider.FileSystem, "application/pdf", Now, null);
        var archive = await BuildArchiveAsync("SELECT 1;", [missing]);

        RestoreJob restore;
        await using (var enqueue = ContextFor(target))
        {
            await using var upload = new MemoryStream(archive);
            restore = await RestoreServiceFor(enqueue, target, TargetStorage).EnqueueAsync(upload, Ct);
        }

        await using (var process = ContextFor(target))
        {
            await RestoreServiceFor(process, target, TargetStorage).ProcessAsync(restore.Id, Ct);
        }

        await using var read = ContextFor(target);
        var job = await new RestoreJobRepository(read).FindByIdAsync(restore.Id, Ct);
        Assert.Equal(RestoreJobStatus.Failed, job!.Status);
        Assert.Contains("1 of the 1 documents", job.ErrorMessage, StringComparison.Ordinal);
        Assert.False(await new SystemStateStore(read).IsReadOnlyAsync(Ct));
    }

    [Fact]
    public async Task O_banco_aceita_um_so_restore_pendente_ou_em_andamento()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        await using (var write = fixture.CreateContext())
        {
            await new RestoreJobRepository(write).AddAsync(RestoreJob.Create(Guid.CreateVersion7(Now), StorageRepository.DefaultRepositoryId, "a", 1, new string('0', 64), 0, Now), Ct);
        }

        await using (var second = fixture.CreateContext())
        {
            var conflict = await Assert.ThrowsAsync<ResourceConflictException>(() =>
                new RestoreJobRepository(second).AddAsync(RestoreJob.Create(Guid.CreateVersion7(Now), StorageRepository.DefaultRepositoryId, "b", 1, new string('0', 64), 0, Now), Ct));
            Assert.Equal("RESTORE_ALREADY_IN_PROGRESS", conflict.ErrorCode);
        }
    }

    [Fact]
    public async Task Reivindicar_backup_pendente_e_atomico_e_devolve_null_quando_nao_ha_nada()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        await using (var empty = fixture.CreateContext())
        {
            Assert.Null(await new BackupJobRepository(empty).ClaimNextPendingAsync(Now, Ct));
        }

        var job = BackupJob.Create(Guid.CreateVersion7(Now), StorageRepository.DefaultRepositoryId, Now);
        await using (var write = fixture.CreateContext())
        {
            await new BackupJobRepository(write).AddAsync(job, Ct);
        }

        await using (var claim = fixture.CreateContext())
        {
            var claimed = await new BackupJobRepository(claim).ClaimNextPendingAsync(Now.AddSeconds(1), Ct);
            Assert.Equal(job.Id, claimed!.Id);
            Assert.Equal(BackupJobStatus.Running, claimed.Status);
        }

        await using var again = fixture.CreateContext();
        Assert.Null(await new BackupJobRepository(again).ClaimNextPendingAsync(Now.AddSeconds(2), Ct));
    }

    [Fact]
    public async Task O_portao_de_somente_leitura_liga_e_desliga()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        await using var context = fixture.CreateContext();
        var store = new SystemStateStore(context);

        Assert.False(await store.IsReadOnlyAsync(Ct));
        await store.SetReadOnlyAsync(true, Now, Ct);
        Assert.True(await store.IsReadOnlyAsync(Ct));
        await store.SetReadOnlyAsync(false, Now, Ct);
        Assert.False(await store.IsReadOnlyAsync(Ct));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private async Task RequireDatabaseAndToolsAsync()
    {
        Assert.SkipUnless(fixture.ConnectionString is not null, fixture.SkipReason ?? "sem banco");

        var tools = await PostgresClientTools.SkipReasonAsync();
        Assert.SkipUnless(tools is null, tools ?? string.Empty);
    }

    private string AdminConnectionString() =>
        new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres", Pooling = false }.ConnectionString;

    private async Task<string> CreateMigratedDatabaseAsync()
    {
        var name = $"{DisposablePrefix}restore_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(AdminConnectionString()))
        {
            await admin.OpenAsync(Ct);
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{name}\";";
            await create.ExecuteNonQueryAsync(Ct);
        }

        _createdDatabases.Add(name);

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
        await using var context = ContextFor(connectionString);
        await context.Database.MigrateAsync(Ct);

        return connectionString;
    }

    private static DocReaderDbContext ContextFor(string connectionString) =>
        new(new DbContextOptionsBuilder<DocReaderDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(1), null))
            .Options);

    private RepositoryFileStorage Facade(DocReaderDbContext context, string storageRoot) => new(
        new StorageRepositoryStore(context, _time),
        new StorageAdapterFactory(context, Options.Create(new StorageOptions { RootPath = storageRoot }), NullLoggerFactory.Instance),
        new AesGcmSecretProtector(Options.Create(_secrets)));

    private PostgresBackupTool Tool(string connectionString)
    {
        // Safety net: nothing in this class may ever point pg_dump or psql at a database that is not disposable.
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        Assert.StartsWith(DisposablePrefix, database, StringComparison.Ordinal);

        return new PostgresBackupTool(connectionString, Options.Create(PostgresClientTools.Options(WorkDirectory)), NullLogger<PostgresBackupTool>.Instance);
    }

    private BackupSignature Signature() => new(Options.Create(_secrets));

    private BackupService BackupServiceFor(DocReaderDbContext context, string connectionString, string storageRoot) => new(
        new BackupJobRepository(context),
        new StorageRepositoryStore(context, _time),
        new TarGzBackupArchiveBuilder(Tool(connectionString), Signature(), Options.Create(PostgresClientTools.Options(WorkDirectory)), _time),
        Facade(context, storageRoot),
        _time,
        NullLogger<BackupService>.Instance);

    private RestoreService RestoreServiceFor(DocReaderDbContext context, string connectionString, string storageRoot) => new(
        new RestoreJobRepository(context),
        new StorageRepositoryStore(context, _time),
        new TarGzBackupArchiveReader(Signature(), Options.Create(PostgresClientTools.Options(WorkDirectory))),
        Tool(connectionString),
        new SystemStateStore(context),
        Facade(context, storageRoot),
        _time,
        NullLogger<RestoreService>.Instance);

    /// <summary>A validly signed archive around a hand-written SQL script, built by the real builder.</summary>
    private async Task<byte[]> BuildArchiveAsync(string sql, IReadOnlyList<BackupManifestEntry>? manifest = null)
    {
        var builder = new TarGzBackupArchiveBuilder(
            new ScriptDumper(sql, manifest ?? []),
            Signature(),
            Options.Create(PostgresClientTools.Options(WorkDirectory)),
            _time);

        await using var built = await builder.BuildAsync((_, _) => Task.FromResult<Stream?>(null), Ct);
        await using var stream = built.OpenRead();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct);

        return buffer.ToArray();
    }

    private sealed class ScriptDumper(string sql, IReadOnlyList<BackupManifestEntry> manifest) : IDatabaseDumper
    {
        public async Task<IReadOnlyList<BackupManifestEntry>> DumpAsync(string outputPath, CancellationToken ct)
        {
            await File.WriteAllTextAsync(outputPath, sql, ct);

            return manifest;
        }
    }
}
