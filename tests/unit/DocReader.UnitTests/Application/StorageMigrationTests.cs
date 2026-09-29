using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using DocReader.Application.StorageMigrations;
using DocReader.Domain.Documents;
using DocReader.Domain.Processing;
using DocReader.Domain.Storage;
using DocReader.Domain.StorageMigrations;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>Moving documents between storage repositories: enqueue and its validation, the batch run, failures and cancel.</summary>
public sealed class StorageMigrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryDocumentStore _documents = new() { Now = Now };
    private readonly InMemoryStorageRepositoryStore _repositories = new();
    private readonly InMemoryFileStorage _storage = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly StorageRepository _target;

    public StorageMigrationTests()
    {
        _target = StorageRepository.Create(Guid.CreateVersion7(Now), "ARQUIVO-DB", "Arquivo", StorageProvider.Database, null, false, true, Now);
        _repositories.Items.Add(_target);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Guid Source => StorageRepository.DefaultRepositoryId;

    private StorageMigrationService Service(InMemoryStorageMigrationJobStore jobs, IFileStorage? storage = null) =>
        new(jobs, _repositories, storage ?? _storage, _clock, NullLogger<StorageMigrationService>.Instance);

    private async Task<Document> StoredDocumentAsync(DateTimeOffset uploadedAt, string? detectedType = null, Guid? repositoryId = null)
    {
        var id = Guid.CreateVersion7(uploadedAt);
        byte[] bytes = [1, 2, 3, 4, (byte)Random.Shared.Next(256)];
        var stored = await _storage.SaveAsync(repositoryId ?? Source, new MemoryStream(bytes), new FileMetadata(id, ".png", "image/png", uploadedAt), Ct);

        var document = Document.Accept(
            id,
            $"DOC-20260929-{Random.Shared.Next(1, 999_999):D6}",
            "Documento Original.PNG",
            stored.StorageKey,
            "image/png",
            bytes.Length,
            new string('d', 64),
            1,
            UploadChannel.Api,
            null,
            null,
            uploadedAt,
            null,
            null,
            repositoryId ?? Source);

        if (detectedType is not null)
        {
            document.RecordClassification(detectedType, 0.9m, uploadedAt);
        }

        document.MarkCompleted(uploadedAt);
        await _documents.AcceptAsync(document, ProcessingJob.CreateForDocument(id, uploadedAt), null, Ct);

        return document;
    }

    // ---- enqueue ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Enfileirar_cria_um_job_pendente()
    {
        var job = await Service(new InMemoryStorageMigrationJobStore(_documents)).EnqueueAsync(Source, _target.Id, null, Ct);

        Assert.Equal(StorageMigrationStatus.Pending, job.Status);
        Assert.Equal(Source, job.SourceRepositoryId);
        Assert.Equal(_target.Id, job.TargetRepositoryId);
        Assert.Equal(Now, job.RequestedAt);
        Assert.True(job.Filter.IsEmpty);
    }

    [Fact]
    public async Task Enfileirar_normaliza_o_filtro()
    {
        var filter = new StorageMigrationFilter(" br_cnh ", null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-3)), null);

        var job = await Service(new InMemoryStorageMigrationJobStore(_documents)).EnqueueAsync(Source, _target.Id, filter, Ct);

        Assert.Equal("BR_CNH", job.FilterDocumentType);
        Assert.Equal(TimeSpan.Zero, job.FilterUploadedFrom!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 3, 0, 0, TimeSpan.Zero), job.FilterUploadedFrom);
    }

    [Fact]
    public async Task Enfileirar_com_origem_igual_ao_destino_e_invalido()
    {
        var error = await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service(new InMemoryStorageMigrationJobStore(_documents)).EnqueueAsync(Source, Source, null, Ct));

        Assert.Equal("STORAGE_MIGRATION_SAME_REPOSITORY", error.ErrorCode);
    }

    [Fact]
    public async Task Enfileirar_com_intervalo_de_datas_invertido_e_invalido()
    {
        var filter = new StorageMigrationFilter(null, null, Now, Now.AddDays(-1));

        var error = await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service(new InMemoryStorageMigrationJobStore(_documents)).EnqueueAsync(Source, _target.Id, filter, Ct));

        Assert.Equal("STORAGE_MIGRATION_INVALID_DATE_RANGE", error.ErrorCode);
    }

    [Fact]
    public async Task Enfileirar_com_repositorio_inexistente_e_422()
    {
        var service = Service(new InMemoryStorageMigrationJobStore(_documents));

        var unknownSource = await Assert.ThrowsAsync<UnprocessableRequestException>(() => service.EnqueueAsync(Guid.NewGuid(), _target.Id, null, Ct));
        var unknownTarget = await Assert.ThrowsAsync<UnprocessableRequestException>(() => service.EnqueueAsync(Source, Guid.NewGuid(), null, Ct));

        Assert.Equal("STORAGE_REPOSITORY_NOT_FOUND", unknownSource.ErrorCode);
        Assert.Equal("STORAGE_REPOSITORY_NOT_FOUND", unknownTarget.ErrorCode);
    }

    [Fact]
    public async Task Enfileirar_para_destino_inativo_e_422()
    {
        var inactive = StorageRepository.Create(Guid.CreateVersion7(Now), "INATIVO", "Inativo", StorageProvider.Database, null, false, false, Now);
        _repositories.Items.Add(inactive);

        var error = await Assert.ThrowsAsync<UnprocessableRequestException>(() =>
            Service(new InMemoryStorageMigrationJobStore(_documents)).EnqueueAsync(Source, inactive.Id, null, Ct));

        Assert.Equal("STORAGE_REPOSITORY_INACTIVE", error.ErrorCode);
    }

    // ---- process ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Processar_move_todos_os_documentos_em_lotes_de_dez_sem_apagar_a_origem()
    {
        var documents = new List<Document>();
        for (var index = 0; index < 25; index++)
        {
            documents.Add(await StoredDocumentAsync(Now.AddMinutes(-index)));
        }

        var originalKeys = documents.ToDictionary(document => document.Id, document => document.StorageKey);
        var jobs = new InMemoryStorageMigrationJobStore(_documents);
        var service = Service(jobs);
        var job = await service.EnqueueAsync(Source, _target.Id, null, Ct);

        _clock.Advance(TimeSpan.FromDays(1));
        await service.ProcessAsync(job.Id, Ct);

        Assert.Equal(StorageMigrationStatus.Completed, job.Status);
        Assert.Equal(25, job.DocumentsMigrated);
        Assert.Equal(0, job.DocumentsFailed);
        Assert.Equal(Now.AddDays(1), job.StartedAt);
        Assert.NotNull(job.CompletedAt);

        // Three batches with documents (10 + 10 + 5) and a last, empty one that ends the run.
        Assert.Equal(4, jobs.BatchesSelected);

        foreach (var document in documents)
        {
            Assert.Equal(_target.Id, document.StorageRepositoryId);
            Assert.NotEqual(originalKeys[document.Id], document.StorageKey);
            Assert.Equal(_storage.Blobs[originalKeys[document.Id]], _storage.Blobs[document.StorageKey]);
            Assert.EndsWith("original.png", document.StorageKey, StringComparison.Ordinal);
            Assert.Equal(DocumentStatus.Completed, document.Status);

            var moved = Assert.Single(document.Events, entry => entry.EventType == DocumentEventTypes.StorageMigrated);
            Assert.True(StorageMigrationEventDetails.TryParse(moved.Details, out var from, out var to));
            Assert.Equal(Source, from);
            Assert.Equal(_target.Id, to);
            Assert.Contains($"migrationJobId={job.Id:D}", moved.Details, StringComparison.Ordinal);
        }

        Assert.Empty(_storage.DeletedKeys);
        Assert.Equal(25, _storage.SavedIn.Count(repositoryId => repositoryId == _target.Id));
        Assert.All(_storage.ReadFrom, repositoryId => Assert.Equal(Source, repositoryId));
    }

    [Fact]
    public async Task Processar_respeita_o_filtro_e_ignora_documentos_de_outro_repositorio_e_expurgados()
    {
        var cnh = await StoredDocumentAsync(Now.AddDays(-1), "BR_CNH");
        var cin = await StoredDocumentAsync(Now.AddDays(-1), "BR_CIN");
        var oldCnh = await StoredDocumentAsync(Now.AddDays(-30), "BR_CNH");
        var purgedCnh = await StoredDocumentAsync(Now.AddDays(-1), "BR_CNH");
        purgedCnh.MarkPurged(Now);

        var other = StorageRepository.Create(Guid.CreateVersion7(Now), "OUTRO", "Outro", StorageProvider.Database, null, false, true, Now);
        _repositories.Items.Add(other);
        var elsewhere = await StoredDocumentAsync(Now.AddDays(-1), "BR_CNH", other.Id);

        var jobs = new InMemoryStorageMigrationJobStore(_documents);
        var service = Service(jobs);
        var job = await service.EnqueueAsync(Source, _target.Id, new StorageMigrationFilter("BR_CNH", null, Now.AddDays(-7), null), Ct);

        await service.ProcessAsync(job.Id, Ct);

        Assert.Equal(1, job.DocumentsMigrated);
        Assert.Equal(_target.Id, cnh.StorageRepositoryId);
        Assert.Equal(Source, cin.StorageRepositoryId);
        Assert.Equal(Source, oldCnh.StorageRepositoryId);
        Assert.Equal(Source, purgedCnh.StorageRepositoryId);
        Assert.Equal(other.Id, elsewhere.StorageRepositoryId);
    }

    [Fact]
    public async Task Um_destino_que_falha_conta_cada_documento_como_falha_e_nao_derruba_o_job()
    {
        for (var index = 0; index < 12; index++)
        {
            await StoredDocumentAsync(Now.AddMinutes(-index));
        }

        var failing = new FailingFileStorage(_storage, _target.Id, () => new HttpRequestException("connection refused"));
        var jobs = new InMemoryStorageMigrationJobStore(_documents);
        var service = Service(jobs, failing);
        var job = await service.EnqueueAsync(Source, _target.Id, null, Ct);

        await service.ProcessAsync(job.Id, Ct);

        Assert.Equal(StorageMigrationStatus.Completed, job.Status);
        Assert.Equal(0, job.DocumentsMigrated);
        Assert.Equal(12, job.DocumentsFailed);
        Assert.Equal(12, failing.FailedSaves);
        Assert.All(_documents.Documents, document =>
        {
            Assert.Equal(Source, document.StorageRepositoryId);
            Assert.DoesNotContain(document.Events, entry => entry.EventType == DocumentEventTypes.StorageMigrated);
        });
    }

    [Fact]
    public async Task Um_documento_sem_original_falha_sozinho_e_os_outros_sao_movidos()
    {
        var healthy = await StoredDocumentAsync(Now.AddMinutes(-2));
        var broken = await StoredDocumentAsync(Now.AddMinutes(-1));
        await _storage.DeleteAsync(Source, broken.StorageKey, Ct);
        var alsoHealthy = await StoredDocumentAsync(Now);

        var jobs = new InMemoryStorageMigrationJobStore(_documents);
        var service = Service(jobs);
        var job = await service.EnqueueAsync(Source, _target.Id, null, Ct);

        await service.ProcessAsync(job.Id, Ct);

        Assert.Equal(StorageMigrationStatus.Completed, job.Status);
        Assert.Equal(2, job.DocumentsMigrated);
        Assert.Equal(1, job.DocumentsFailed);
        Assert.Equal(_target.Id, healthy.StorageRepositoryId);
        Assert.Equal(_target.Id, alsoHealthy.StorageRepositoryId);
        Assert.Equal(Source, broken.StorageRepositoryId);
    }

    [Fact]
    public async Task Processar_de_novo_nao_refaz_o_que_ja_foi_movido()
    {
        for (var index = 0; index < 3; index++)
        {
            await StoredDocumentAsync(Now.AddMinutes(-index));
        }

        var jobs = new InMemoryStorageMigrationJobStore(_documents);
        var service = Service(jobs);
        var job = await service.EnqueueAsync(Source, _target.Id, null, Ct);

        await service.ProcessAsync(job.Id, Ct);
        var savesAfterFirstRun = _storage.SavedIn.Count;

        // A completed job is not run again, and even a forced second pass would find nothing left on the source.
        await service.ProcessAsync(job.Id, Ct);

        Assert.Equal(savesAfterFirstRun, _storage.SavedIn.Count);
        Assert.Equal(3, job.DocumentsMigrated);
        Assert.All(_documents.Documents, document =>
            Assert.Single(document.Events, entry => entry.EventType == DocumentEventTypes.StorageMigrated));
    }

    // ---- cancel -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancelar_um_job_pendente_impede_que_ele_rode()
    {
        await StoredDocumentAsync(Now);
        var jobs = new InMemoryStorageMigrationJobStore(_documents);
        var service = Service(jobs);
        var job = await service.EnqueueAsync(Source, _target.Id, null, Ct);

        var cancelled = await service.CancelAsync(job.Id, Ct);
        await service.ProcessAsync(job.Id, Ct);

        Assert.Equal(StorageMigrationStatus.Cancelled, cancelled.Status);
        Assert.Equal(Now, cancelled.CompletedAt);
        Assert.Null(await jobs.ClaimNextPendingAsync(Now, Ct));
        Assert.All(_documents.Documents, document => Assert.Equal(Source, document.StorageRepositoryId));
    }

    [Fact]
    public async Task Cancelar_durante_a_execucao_para_antes_do_proximo_lote_e_nao_desfaz_o_que_foi_movido()
    {
        for (var index = 0; index < 25; index++)
        {
            await StoredDocumentAsync(Now.AddMinutes(-index));
        }

        var jobs = new InMemoryStorageMigrationJobStore(_documents);
        var service = Service(jobs);
        var job = await service.EnqueueAsync(Source, _target.Id, null, Ct);

        var checks = 0;
        jobs.BeforeStatusCheck = running =>
        {
            // The first check lets batch one through; before batch two a caller cancels.
            if (++checks == 2)
            {
                running.Cancel(Now);
            }
        };

        await service.ProcessAsync(job.Id, Ct);

        Assert.Equal(StorageMigrationStatus.Cancelled, job.Status);
        Assert.Equal(StorageMigrationService.BatchSize, job.DocumentsMigrated);
        Assert.Equal(StorageMigrationService.BatchSize, _documents.Documents.Count(document => document.StorageRepositoryId == _target.Id));
        Assert.Equal(25 - StorageMigrationService.BatchSize, _documents.Documents.Count(document => document.StorageRepositoryId == Source));
        Assert.Empty(_storage.DeletedKeys);
    }

    [Theory]
    [InlineData(StorageMigrationStatus.Completed)]
    [InlineData(StorageMigrationStatus.Failed)]
    [InlineData(StorageMigrationStatus.Cancelled)]
    public async Task Cancelar_um_job_encerrado_e_conflito(StorageMigrationStatus status)
    {
        var jobs = new InMemoryStorageMigrationJobStore(_documents);
        var service = Service(jobs);
        var job = await service.EnqueueAsync(Source, _target.Id, null, Ct);
        job.MarkRunning(Now);

        switch (status)
        {
            case StorageMigrationStatus.Completed:
                job.MarkCompleted(Now);
                break;
            case StorageMigrationStatus.Failed:
                job.MarkFailed(Now, "boom");
                break;
            default:
                job.Cancel(Now);
                break;
        }

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => service.CancelAsync(job.Id, Ct));

        Assert.Equal("STORAGE_MIGRATION_NOT_CANCELLABLE", error.ErrorCode);
        Assert.Equal(status, job.Status);
    }

    [Fact]
    public async Task Cancelar_ou_consultar_job_inexistente_e_not_found()
    {
        var service = Service(new InMemoryStorageMigrationJobStore(_documents));

        var cancel = await Assert.ThrowsAsync<ResourceNotFoundException>(() => service.CancelAsync(Guid.NewGuid(), Ct));
        var get = await Assert.ThrowsAsync<ResourceNotFoundException>(() => service.GetAsync(Guid.NewGuid(), Ct));

        Assert.Equal("storage-migration-job", cancel.Resource);
        Assert.Equal("storage-migration-job", get.Resource);
    }
}
