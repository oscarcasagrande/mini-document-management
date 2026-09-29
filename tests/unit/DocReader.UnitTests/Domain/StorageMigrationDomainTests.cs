using DocReader.Domain.Documents;
using DocReader.Domain.Files;
using DocReader.Domain.Storage;
using DocReader.Domain.StorageMigrations;
using Xunit;

namespace DocReader.UnitTests.Domain;

/// <summary>The domain side of a storage migration: repointing a document, the event it leaves, and the job's lifecycle.</summary>
public sealed class StorageMigrationDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static Document StoredDocument()
    {
        var id = Guid.CreateVersion7(Now);
        var document = Document.Accept(
            id, "DOC-20260929-000001", "a.pdf", $"documents/2026/09/29/{id:D}/original.pdf", "application/pdf", 10,
            new string('a', 64), 1, UploadChannel.Api, null, null, Now);
        document.MarkCompleted(Now);

        return document;
    }

    [Fact]
    public void Migrar_troca_repositorio_e_chave_e_registra_o_evento_sem_mudar_o_status()
    {
        var document = StoredDocument();
        var target = Guid.CreateVersion7(Now);
        var jobId = Guid.CreateVersion7(Now);

        document.MigrateStorageRepository(target, "blobs/x", Now.AddHours(1), jobId);

        Assert.Equal(target, document.StorageRepositoryId);
        Assert.Equal("blobs/x", document.StorageKey);
        Assert.Equal(DocumentStatus.Completed, document.Status);

        var moved = Assert.Single(document.Events, entry => entry.EventType == DocumentEventTypes.StorageMigrated);
        Assert.Equal(DocumentStatus.Completed, moved.Stage);
        Assert.Equal(Now.AddHours(1), moved.OccurredAt);
        Assert.Equal(
            $"fromRepositoryId={StorageRepository.DefaultRepositoryId:D} toRepositoryId={target:D} migrationJobId={jobId:D}",
            moved.Details);
    }

    [Fact]
    public void Migrar_para_o_proprio_repositorio_e_recusado()
    {
        var document = StoredDocument();

        Assert.Throws<InvalidOperationException>(() =>
            document.MigrateStorageRepository(StorageRepository.DefaultRepositoryId, "documents/x", Now));
    }

    [Fact]
    public void Os_detalhes_do_evento_voltam_a_origem_e_o_destino()
    {
        var from = Guid.CreateVersion7(Now);
        var to = Guid.CreateVersion7(Now.AddSeconds(1));

        Assert.True(StorageMigrationEventDetails.TryParse(StorageMigrationEventDetails.Format(from, to, null), out var parsedFrom, out var parsedTo));
        Assert.Equal(from, parsedFrom);
        Assert.Equal(to, parsedTo);

        Assert.True(StorageMigrationEventDetails.TryParse(StorageMigrationEventDetails.Format(from, to, Guid.NewGuid()), out parsedFrom, out parsedTo));
        Assert.Equal(from, parsedFrom);
        Assert.Equal(to, parsedTo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("reason=RETENTION_EXPIRED")]
    [InlineData("fromRepositoryId=nao-e-guid toRepositoryId=00000000-0000-0000-0000-000000000001")]
    public void Detalhes_sem_origem_e_destino_validos_nao_viram_historico(string? details)
    {
        Assert.False(StorageMigrationEventDetails.TryParse(details, out _, out _));
    }

    [Fact]
    public void O_job_nao_aceita_origem_igual_ao_destino()
    {
        var repository = Guid.CreateVersion7(Now);

        Assert.Throws<ArgumentException>(() => StorageMigrationJob.Create(Guid.CreateVersion7(Now), repository, repository, null, Now));
    }

    [Fact]
    public void Um_job_cancelado_durante_a_execucao_continua_cancelado_ao_terminar()
    {
        var job = StorageMigrationJob.Create(Guid.CreateVersion7(Now), Guid.CreateVersion7(Now), Guid.CreateVersion7(Now.AddSeconds(1)), null, Now);
        job.MarkRunning(Now);
        job.Cancel(Now.AddMinutes(1));

        job.RecordBatch(3, 1);
        job.MarkCompleted(Now.AddMinutes(2));
        job.MarkFailed(Now.AddMinutes(3), "boom");

        Assert.Equal(StorageMigrationStatus.Cancelled, job.Status);
        Assert.Equal(Now.AddMinutes(1), job.CompletedAt);
        Assert.Equal(3, job.DocumentsMigrated);
        Assert.Equal(1, job.DocumentsFailed);
        Assert.Null(job.ErrorMessage);
        Assert.Throws<InvalidOperationException>(() => job.Cancel(Now));
    }

    [Theory]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    [InlineData("image/tiff", ".tif")]
    [InlineData("application/octet-stream", "")]
    public void A_extensao_vem_do_mime_detectado(string mimeType, string extension)
    {
        Assert.Equal(extension, FileSignatureInspector.ExtensionForMimeType(mimeType));
    }
}
