using DocReader.Application.Classification;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Domain.Documents;
using DocReader.Domain.Processing;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>
/// Lookup by the caller's own reference: the reference is not unique, so the latest upload wins, and the list can
/// be narrowed by part of it.
/// </summary>
public sealed class ExternalReferenceLookupTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DocumentQueryService QueryService(InMemoryDocumentStore store) =>
        new(store, new InMemoryFileStorage(), new RulesDocumentClassifier(), [], NullLogger<DocumentQueryService>.Instance);

    private static async Task<Document> UploadAsync(
        InMemoryDocumentStore store,
        string? externalReference,
        DateTimeOffset uploadedAt,
        int sequence)
    {
        var id = Guid.CreateVersion7(uploadedAt);
        var document = Document.Accept(
            id, $"DOC-20260925-{sequence:D6}", "doc.png", $"documents/{id:D}/original.png", "image/png",
            1024, new string('a', 64), 1, UploadChannel.Api, externalReference, null, uploadedAt);
        document.MarkQueued(uploadedAt);

        await store.AcceptAsync(document, ProcessingJob.CreateForDocument(id, uploadedAt), null, Ct);

        return document;
    }

    [Fact]
    public async Task Devolve_o_documento_da_referencia()
    {
        var store = new InMemoryDocumentStore();
        var wanted = await UploadAsync(store, "PEDIDO-1", Now, 1);
        await UploadAsync(store, "PEDIDO-2", Now.AddMinutes(1), 2);

        var found = await QueryService(store).GetLatestByExternalReferenceAsync("PEDIDO-1", Ct);

        Assert.Equal(wanted.Id, found.Id);
    }

    [Fact]
    public async Task Com_mais_de_um_documento_na_mesma_referencia_devolve_o_mais_recente()
    {
        var store = new InMemoryDocumentStore();
        await UploadAsync(store, "PEDIDO-1", Now, 1);
        var latest = await UploadAsync(store, "PEDIDO-1", Now.AddHours(2), 2);
        await UploadAsync(store, "PEDIDO-1", Now.AddHours(1), 3);

        var found = await QueryService(store).GetLatestByExternalReferenceAsync("PEDIDO-1", Ct);

        Assert.Equal(latest.Id, found.Id);
    }

    [Fact]
    public async Task Espacos_nas_pontas_sao_ignorados()
    {
        var store = new InMemoryDocumentStore();
        var document = await UploadAsync(store, "PEDIDO-1", Now, 1);

        var found = await QueryService(store).GetLatestByExternalReferenceAsync("  PEDIDO-1 ", Ct);

        Assert.Equal(document.Id, found.Id);
    }

    [Fact]
    public async Task A_busca_e_exata_e_diferencia_maiusculas()
    {
        var store = new InMemoryDocumentStore();
        await UploadAsync(store, "PEDIDO-1", Now, 1);
        var service = QueryService(store);

        await Assert.ThrowsAsync<DocumentNotFoundException>(() => service.GetLatestByExternalReferenceAsync("pedido-1", Ct));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => service.GetLatestByExternalReferenceAsync("PEDIDO", Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Referencia_em_branco_nao_encontra_nada_nem_documento_sem_referencia(string reference)
    {
        var store = new InMemoryDocumentStore();
        await UploadAsync(store, null, Now, 1);

        await Assert.ThrowsAsync<DocumentNotFoundException>(() => QueryService(store).GetLatestByExternalReferenceAsync(reference, Ct));
    }

    [Fact]
    public async Task Referencia_maior_que_a_coluna_e_not_found_sem_ir_ao_banco()
    {
        var error = await Assert.ThrowsAsync<DocumentNotFoundException>(() =>
            QueryService(new InMemoryDocumentStore()).GetLatestByExternalReferenceAsync(new string('x', 300), Ct));

        Assert.True(error.Identifier.Length < 200);
    }

    [Fact]
    public async Task Referencia_inexistente_e_not_found()
    {
        var error = await Assert.ThrowsAsync<DocumentNotFoundException>(() =>
            QueryService(new InMemoryDocumentStore()).GetLatestByExternalReferenceAsync("NADA", Ct));

        Assert.Contains("NADA", error.Identifier, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lista_filtra_por_parte_da_referencia_sem_diferenciar_maiusculas()
    {
        var store = new InMemoryDocumentStore();
        await UploadAsync(store, "PEDIDO-100", Now, 1);
        await UploadAsync(store, "pedido-200", Now.AddMinutes(1), 2);
        await UploadAsync(store, "FATURA-1", Now.AddMinutes(2), 3);
        await UploadAsync(store, null, Now.AddMinutes(3), 4);

        var page = await QueryService(store).ListAsync(
            new DocumentListFilter(null, null, null, null, null, null, null, 1, 20, ExternalReference: "Pedido"), Ct);

        Assert.Equal(["pedido-200", "PEDIDO-100"], page.Items.Select(item => item.ExternalReference));
        Assert.Equal(2, page.TotalCount);
    }
}
