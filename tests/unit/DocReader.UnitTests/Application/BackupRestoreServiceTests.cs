using System.Security.Cryptography;
using DocReader.Application.Backup;
using DocReader.Application.Errors;
using DocReader.Application.Options;
using DocReader.Domain.Backup;
using DocReader.Domain.Storage;
using DocReader.Infrastructure.Backup;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>
/// The backup and restore use cases over in-memory stores, with the real archive builder and reader: what a backup
/// copies and what it only lists, where it saves the archive, and how a restore raises and lowers the read-only gate,
/// fails, verifies and writes files back.
/// </summary>
public sealed class BackupRestoreServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly string _work = Path.Combine(Path.GetTempPath(), "docreader-unit-backup", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(Now);
    private readonly SecretsOptions _secrets = new() { EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
    private readonly InMemoryStorageRepositoryStore _repositories = new();
    private readonly InMemoryFileStorage _storage = new();
    private readonly InMemoryBackupJobStore _backups = new();
    private readonly InMemoryRestoreJobStore _restores = new();
    private readonly InMemorySystemState _state = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

    private IOptions<BackupOptions> BackupOptions => Options.Create(new BackupOptions { WorkingDirectory = _work });

    private BackupSignature Signature => new(Options.Create(_secrets));

    private BackupService BackupService(string sql, IReadOnlyList<BackupManifestEntry> manifest) => new(
        _backups,
        _repositories,
        new TarGzBackupArchiveBuilder(new ScriptDumper(sql, manifest), Signature, BackupOptions, _time),
        _storage,
        _time,
        NullLogger<BackupService>.Instance);

    private RestoreService RestoreService(RecordingDatabaseRestorer restorer) => new(
        _restores,
        _repositories,
        new TarGzBackupArchiveReader(Signature, BackupOptions),
        restorer,
        _state,
        _storage,
        _time,
        NullLogger<RestoreService>.Instance);

    private StorageRepository AddRepository(StorageProvider provider, bool active = true)
    {
        var repository = StorageRepository.Create(Guid.CreateVersion7(Now), $"R{_repositories.Items.Count}", "Repo", provider, null, false, active, Now);
        _repositories.Items.Add(repository);

        return repository;
    }

    /// <summary>A document whose file is in the fake storage at the key the storage itself derives.</summary>
    private async Task<(BackupManifestEntry Entry, byte[] Bytes)> StoredDocumentAsync(StorageRepository repository)
    {
        var id = Guid.CreateVersion7(Now);
        var bytes = RandomNumberGenerator.GetBytes(512);
        await using var content = new MemoryStream(bytes);
        var stored = await _storage.SaveAsync(repository.Id, content, new DocReader.Application.Abstractions.FileMetadata(id, ".pdf", "application/pdf", Now), Ct);

        return (new BackupManifestEntry(id, repository.Id, stored.StorageKey, repository.Provider, "application/pdf", Now, null), bytes);
    }

    private async Task<byte[]> CompletedArchiveAsync(BackupJob job)
    {
        var completed = await _backups.FindByIdAsync(job.Id, Ct);
        Assert.Equal(BackupJobStatus.Completed, completed!.Status);

        return _storage.Blobs[completed.StorageKey!];
    }

    // ---- backup -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Backup_copia_arquivos_locais_so_lista_os_da_nuvem_e_salva_no_repositorio_padrao()
    {
        var database = AddRepository(StorageProvider.Database);
        var s3 = AddRepository(StorageProvider.AwsS3);
        var (onDisk, diskBytes) = await StoredDocumentAsync(_repositories.Default);
        var (inDatabase, databaseBytes) = await StoredDocumentAsync(database);
        var (inCloud, _) = await StoredDocumentAsync(s3);
        _storage.ReadFrom.Clear();

        var service = BackupService("SELECT 1;", [onDisk, inDatabase, inCloud]);
        var job = await service.EnqueueAsync(null, Ct);
        Assert.Equal(BackupJobStatus.Pending, job.Status);
        Assert.Equal(StorageRepository.DefaultRepositoryId, job.StorageRepositoryId);

        await service.ProcessAsync(job.Id, Ct);

        var completed = (await _backups.FindByIdAsync(job.Id, Ct))!;
        Assert.Equal(BackupJobStatus.Completed, completed.Status);
        Assert.Equal(3, completed.DocumentCount);
        Assert.Equal(2, completed.FilesArchived);
        Assert.Equal($"backup-{job.Id:D}.tar.gz", completed.FileName);
        Assert.EndsWith($"{job.Id:D}/original.tgz", completed.StorageKey, StringComparison.Ordinal);
        Assert.DoesNotContain(s3.Id, _storage.ReadFrom);
        Assert.Equal(StorageRepository.DefaultRepositoryId, _storage.SavedIn[^1]);

        var archive = await CompletedArchiveAsync(job);
        Assert.Equal(completed.SizeBytes, archive.Length);
        Assert.Equal(completed.ChecksumSha256, Convert.ToHexStringLower(SHA256.HashData(archive)));

        await using var extracted = await new TarGzBackupArchiveReader(Signature, BackupOptions).ExtractAndVerifyAsync(new MemoryStream(archive), Ct);
        Assert.Null(extracted.Documents.Single(entry => entry.DocumentId == inCloud.DocumentId).ArchivePath);

        await using (var file = await extracted.OpenArchivedFileAsync($"files/{onDisk.DocumentId:D}", Ct))
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, Ct);
            Assert.Equal(diskBytes, buffer.ToArray());
        }

        await using (var file = await extracted.OpenArchivedFileAsync($"files/{inDatabase.DocumentId:D}", Ct))
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, Ct);
            Assert.Equal(databaseBytes, buffer.ToArray());
        }
    }

    [Fact]
    public async Task Backup_pode_ir_para_um_repositorio_de_nuvem_nomeado()
    {
        var s3 = AddRepository(StorageProvider.AwsS3);
        var service = BackupService("SELECT 1;", []);

        var job = await service.EnqueueAsync(s3.Id, Ct);
        await service.ProcessAsync(job.Id, Ct);

        Assert.Equal(s3.Id, job.StorageRepositoryId);
        Assert.Equal(s3.Id, _storage.SavedIn.Single());
        Assert.Equal(BackupJobStatus.Completed, job.Status);
    }

    [Fact]
    public async Task Backup_recusa_repositorio_de_banco_inativo_ou_inexistente()
    {
        var service = BackupService("SELECT 1;", []);

        var database = AddRepository(StorageProvider.Database);
        Assert.Equal("BACKUP_TARGET_UNSUPPORTED", (await Assert.ThrowsAsync<UnprocessableRequestException>(() => service.EnqueueAsync(database.Id, Ct))).ErrorCode);

        var inactive = AddRepository(StorageProvider.FileSystem, active: false);
        Assert.Equal("STORAGE_REPOSITORY_INACTIVE", (await Assert.ThrowsAsync<UnprocessableRequestException>(() => service.EnqueueAsync(inactive.Id, Ct))).ErrorCode);

        Assert.Equal("STORAGE_REPOSITORY_NOT_FOUND", (await Assert.ThrowsAsync<UnprocessableRequestException>(() => service.EnqueueAsync(Guid.NewGuid(), Ct))).ErrorCode);
        Assert.Empty(_backups.Items);
    }

    [Fact]
    public async Task Backup_que_falha_fica_FAILED_com_o_motivo_e_nao_salva_nada()
    {
        var service = new BackupService(
            _backups,
            _repositories,
            new TarGzBackupArchiveBuilder(new FailingDumper(), Signature, BackupOptions, _time),
            _storage,
            _time,
            NullLogger<BackupService>.Instance);

        var job = await service.EnqueueAsync(null, Ct);
        await service.ProcessAsync(job.Id, Ct);

        Assert.Equal(BackupJobStatus.Failed, job.Status);
        Assert.Equal("pg_dump exited with code 1. pg_dump: error: connection refused", job.ErrorMessage);
        Assert.Empty(_storage.SavedIn);
        Assert.False(Directory.Exists(_work) && Directory.EnumerateFileSystemEntries(_work).Any(), "The workspace of a failed backup must be removed.");
    }

    [Fact]
    public async Task Download_de_backup_nao_concluido_e_conflito()
    {
        var service = BackupService("SELECT 1;", []);
        var job = await service.EnqueueAsync(null, Ct);

        Assert.Equal("BACKUP_NOT_COMPLETED", (await Assert.ThrowsAsync<ResourceConflictException>(() => service.OpenArchiveAsync(job.Id, Ct))).ErrorCode);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => service.GetAsync(Guid.NewGuid(), Ct));
    }

    // ---- restore ----------------------------------------------------------------------------------------------

    private async Task<byte[]> BackupArchiveAsync(IReadOnlyList<BackupManifestEntry> manifest, string sql = "SELECT 'restored';")
    {
        var service = BackupService(sql, manifest);
        var job = await service.EnqueueAsync(null, Ct);
        await service.ProcessAsync(job.Id, Ct);

        return await CompletedArchiveAsync(job);
    }

    [Fact]
    public async Task Restore_levanta_o_portao_durante_o_psql_devolve_os_arquivos_e_abaixa_o_portao()
    {
        var (entry, bytes) = await StoredDocumentAsync(_repositories.Default);
        var archive = await BackupArchiveAsync([entry]);

        // The "volume was lost": the file is no longer in the storage.
        await _storage.DeleteAsync(entry.StorageRepositoryId, entry.StorageKey, Ct);

        var restorer = new RecordingDatabaseRestorer(_state);
        var service = RestoreService(restorer);

        var job = await service.EnqueueAsync(new MemoryStream(archive), Ct);
        Assert.Equal(RestoreJobStatus.Pending, job.Status);
        Assert.Equal(1, job.DocumentCount);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(archive)), job.ArchiveChecksumSha256);
        Assert.True(_storage.Blobs.ContainsKey(job.ArchiveStorageKey));

        await service.ProcessAsync(job.Id, Ct);

        Assert.Equal(RestoreJobStatus.Completed, job.Status);
        Assert.Null(job.ErrorMessage);
        Assert.Equal(1, job.FilesRestored);
        Assert.Equal("SELECT 'restored';", restorer.RestoredSql);
        Assert.True(restorer.ReadOnlyDuringRestore);
        Assert.Equal([true, false], _state.Changes);
        Assert.Equal(1, _restores.ForgetCalls);
        Assert.Equal(bytes, _storage.Blobs[entry.StorageKey]);
        Assert.False(_storage.Blobs.ContainsKey(job.ArchiveStorageKey), "The uploaded archive is removed once the job ends.");
    }

    [Fact]
    public async Task Restore_cujo_psql_falha_fica_FAILED_abaixa_o_portao_e_nao_escreve_arquivos()
    {
        var (entry, _) = await StoredDocumentAsync(_repositories.Default);
        var archive = await BackupArchiveAsync([entry]);
        await _storage.DeleteAsync(entry.StorageRepositoryId, entry.StorageKey, Ct);

        var restorer = new RecordingDatabaseRestorer(_state) { FailWith = new PostgresToolException("psql exited with code 3. ERROR:  division by zero") };
        var service = RestoreService(restorer);
        var job = await service.EnqueueAsync(new MemoryStream(archive), Ct);

        await service.ProcessAsync(job.Id, Ct);

        Assert.Equal(RestoreJobStatus.Failed, job.Status);
        Assert.Equal("psql exited with code 3. ERROR:  division by zero", job.ErrorMessage);
        Assert.Equal([true, false], _state.Changes);
        Assert.False(_state.IsReadOnly);
        Assert.False(_storage.Blobs.ContainsKey(entry.StorageKey));
    }

    [Fact]
    public async Task Restore_com_documento_do_manifesto_ausente_do_banco_falha_depois_do_commit()
    {
        var (entry, _) = await StoredDocumentAsync(_repositories.Default);
        var archive = await BackupArchiveAsync([entry]);

        var restorer = new RecordingDatabaseRestorer(_state) { ExistingDocuments = [] };
        var service = RestoreService(restorer);
        var job = await service.EnqueueAsync(new MemoryStream(archive), Ct);

        await service.ProcessAsync(job.Id, Ct);

        Assert.Equal(RestoreJobStatus.Failed, job.Status);
        Assert.Contains("1 of the 1 documents listed in the storage manifest are not in it", job.ErrorMessage, StringComparison.Ordinal);
        Assert.False(_state.IsReadOnly);
    }

    [Fact]
    public async Task Restore_recusa_arquivo_adulterado_antes_de_enfileirar()
    {
        var archive = await BackupArchiveAsync([]);
        archive[archive.Length / 2] ^= 0xFF;

        var service = RestoreService(new RecordingDatabaseRestorer(_state));

        await Assert.ThrowsAsync<BackupArchiveInvalidException>(() => service.EnqueueAsync(new MemoryStream(archive), Ct));
        Assert.Empty(_restores.Items);
        Assert.Empty(_state.Changes);
    }

    [Fact]
    public async Task Um_segundo_restore_com_outro_pendente_e_conflito()
    {
        var archive = await BackupArchiveAsync([]);
        var service = RestoreService(new RecordingDatabaseRestorer(_state));

        await service.EnqueueAsync(new MemoryStream(archive), Ct);
        var conflict = await Assert.ThrowsAsync<ResourceConflictException>(() => service.EnqueueAsync(new MemoryStream(archive), Ct));

        Assert.Equal("RESTORE_ALREADY_IN_PROGRESS", conflict.ErrorCode);
        Assert.Single(_restores.Items);
    }

    [Fact]
    public async Task Na_partida_o_restore_interrompido_vira_FAILED_e_o_portao_desce()
    {
        var job = RestoreJob.Create(Guid.CreateVersion7(Now), StorageRepository.DefaultRepositoryId, "k", 1, new string('0', 64), 0, Now);
        job.MarkRunning(Now);
        _restores.Items.Add(job);
        _state.IsReadOnly = true;

        await RestoreService(new RecordingDatabaseRestorer(_state)).RecoverInterruptedAsync(Ct);

        Assert.Equal(RestoreJobStatus.Failed, job.Status);
        Assert.StartsWith("Interrupted", job.ErrorMessage, StringComparison.Ordinal);
        Assert.False(_state.IsReadOnly);
    }

    private sealed class FailingDumper : IDatabaseDumper
    {
        public Task<IReadOnlyList<BackupManifestEntry>> DumpAsync(string outputPath, CancellationToken ct) =>
            throw new PostgresToolException("pg_dump exited with code 1. pg_dump: error: connection refused");
    }
}
