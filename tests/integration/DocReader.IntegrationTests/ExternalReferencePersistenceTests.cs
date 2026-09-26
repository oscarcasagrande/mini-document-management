using DocReader.Application.Documents;
using DocReader.Domain.Documents;
using DocReader.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// The external reference against a real PostgreSQL: exact lookup returning the latest upload, the partial filter of the
/// list with LIKE metacharacters, and the index that serves the lookup.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ExternalReferencePersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Document DocumentWith(string? externalReference, DateTimeOffset uploadedAt, int sequence)
    {
        var id = Guid.CreateVersion7(uploadedAt);
        var document = Document.Accept(
            id,
            $"DOC-20260925-{sequence:D6}",
            "doc.png",
            $"documents/2026/09/25/{id:D}/original.png",
            "image/png",
            1024,
            new string('c', 64),
            1,
            UploadChannel.Api,
            externalReference,
            null,
            uploadedAt);
        document.MarkQueued(uploadedAt);

        return document;
    }

    private async Task SeedAsync(params Document[] documents)
    {
        await using var write = fixture.CreateContext();
        write.Documents.AddRange(documents);
        await write.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task A_consulta_devolve_o_mais_recente_quando_a_referencia_se_repete()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        var oldest = DocumentWith("PEDIDO-1", Now, 1);
        var latest = DocumentWith("PEDIDO-1", Now.AddHours(2), 2);
        var middle = DocumentWith("PEDIDO-1", Now.AddHours(1), 3);
        var other = DocumentWith("PEDIDO-2", Now.AddHours(3), 4);
        await SeedAsync(oldest, latest, middle, other);

        await using var read = fixture.CreateContext();
        var found = await new DocumentRepository(read).FindLatestByExternalReferenceAsync("PEDIDO-1", Ct);

        Assert.Equal(latest.Id, found!.Id);
    }

    [Fact]
    public async Task A_consulta_e_exata_e_nao_confunde_prefixo_maiuscula_nem_curinga()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        await SeedAsync(DocumentWith("PEDIDO-1", Now, 1), DocumentWith(null, Now.AddMinutes(1), 2));

        await using var read = fixture.CreateContext();
        var repository = new DocumentRepository(read);

        Assert.NotNull(await repository.FindLatestByExternalReferenceAsync("PEDIDO-1", Ct));
        Assert.Null(await repository.FindLatestByExternalReferenceAsync("pedido-1", Ct));
        Assert.Null(await repository.FindLatestByExternalReferenceAsync("PEDIDO", Ct));
        Assert.Null(await repository.FindLatestByExternalReferenceAsync("PEDIDO-%", Ct));
        Assert.Null(await repository.FindLatestByExternalReferenceAsync("PEDIDO-_", Ct));
    }

    [Fact]
    public async Task A_consulta_traz_o_documento_com_seu_produto_e_sem_documento_de_outra_referencia()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        await SeedAsync(DocumentWith("ABC/1?x=y&z", Now, 1), DocumentWith("ABC/2", Now.AddMinutes(1), 2));

        await using var read = fixture.CreateContext();
        var found = await new DocumentRepository(read).FindLatestByExternalReferenceAsync("ABC/1?x=y&z", Ct);

        Assert.Equal("DOC-20260925-000001", found!.Protocol);
    }

    [Fact]
    public async Task A_lista_filtra_por_parte_da_referencia_e_trata_curingas_como_texto()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        await SeedAsync(
            DocumentWith("PEDIDO-100", Now, 1),
            DocumentWith("pedido-200", Now.AddMinutes(1), 2),
            DocumentWith("DESCONTO 100%", Now.AddMinutes(2), 3),
            DocumentWith("FATURA_1", Now.AddMinutes(3), 4),
            DocumentWith("FATURAX1", Now.AddMinutes(4), 5),
            DocumentWith(null, Now.AddMinutes(5), 6));

        await using var read = fixture.CreateContext();
        var repository = new DocumentRepository(read);

        static DocumentListFilter By(string reference) =>
            new(null, null, null, null, null, null, null, 1, 20, ExternalReference: reference);

        Assert.Equal(["pedido-200", "PEDIDO-100"], (await repository.ListAsync(By("Pedido"), Ct)).Items.Select(item => item.ExternalReference));
        Assert.Equal(["DESCONTO 100%"], (await repository.ListAsync(By("100%"), Ct)).Items.Select(item => item.ExternalReference));
        Assert.Equal(["FATURA_1"], (await repository.ListAsync(By("A_1"), Ct)).Items.Select(item => item.ExternalReference));
        Assert.Equal(["DESCONTO 100%"], (await repository.ListAsync(By("%"), Ct)).Items.Select(item => item.ExternalReference));
        Assert.Equal(6, (await repository.ListAsync(By("  "), Ct)).Items.Count);
    }

    [Fact]
    public async Task O_indice_da_referencia_existe_e_e_parcial()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        await using var context = fixture.CreateContext();
        var definition = await context.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ix_documents_external_reference_uploaded_at'")
            .SingleAsync(Ct);

        Assert.Contains("(external_reference, uploaded_at)", definition, StringComparison.Ordinal);
        Assert.Contains("external_reference IS NOT NULL", definition, StringComparison.Ordinal);
    }
}
