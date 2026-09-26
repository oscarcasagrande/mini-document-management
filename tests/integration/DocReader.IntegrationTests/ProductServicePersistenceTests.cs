using DocReader.Application.Catalog;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Domain.Catalog;
using DocReader.Domain.Documents;
using DocReader.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// Products and services against a real PostgreSQL: the unique code, the filters, the link with a
/// document and the restriction that keeps a product with documents from being deleted.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ProductServicePersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ProductService Product(string code, string name = "Nome", bool active = true) =>
        ProductService.Create(Guid.CreateVersion7(Now), code, name, active, Now);

    private static Document DocumentFor(Guid? productServiceId, string? externalReference = null)
    {
        var id = Guid.CreateVersion7(Now);
        var document = Document.Accept(
            id,
            $"DOC-20260925-{Random.Shared.Next(1, 999_999):D6}",
            "doc.png",
            $"documents/2026/09/25/{id:D}/original.png",
            "image/png",
            1024,
            new string('c', 64),
            1,
            UploadChannel.Api,
            externalReference,
            null,
            Now,
            productServiceId);
        document.MarkQueued(Now);

        return document;
    }

    [Fact]
    public async Task Codigo_repetido_e_recusado_pelo_banco_como_conflito()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        await using var context = fixture.CreateContext();
        var repository = new ProductServiceRepository(context);
        await repository.AddAsync(Product("conta-pj"), Ct);

        await using var second = fixture.CreateContext();
        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            new ProductServiceRepository(second).AddAsync(Product("CONTA-PJ"), Ct));

        Assert.Equal("PRODUCT_SERVICE_CODE_EXISTS", error.ErrorCode);
    }

    [Fact]
    public async Task Produto_gravado_volta_pelo_id_e_pelo_codigo()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        var product = Product("conta-pj", "Conta PJ");
        await using (var write = fixture.CreateContext())
        {
            await new ProductServiceRepository(write).AddAsync(product, Ct);
        }

        await using var read = fixture.CreateContext();
        var repository = new ProductServiceRepository(read);

        Assert.Equal("Conta PJ", (await repository.FindByIdAsync(product.Id, Ct))!.Name);
        Assert.Equal(product.Id, (await repository.FindByCodeAsync("CONTA-PJ", Ct))!.Id);
        Assert.Null(await repository.FindByCodeAsync("OUTRO", Ct));
    }

    [Fact]
    public async Task Lista_filtra_por_codigo_nome_e_ativo_tratando_curinga_como_texto()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        await using (var write = fixture.CreateContext())
        {
            var repository = new ProductServiceRepository(write);
            await repository.AddAsync(Product("a-1", "Conta 100%"), Ct);
            await repository.AddAsync(Product("a-2", "Cartao", active: false), Ct);
            await repository.AddAsync(Product("b_1", "Conta digital"), Ct);
        }

        await using var read = fixture.CreateContext();
        var query = new ProductServiceRepository(read);

        Assert.Equal(["A-1", "A-2"], (await query.ListAsync(new ProductServiceFilter("a-", null, null, 1, 10), Ct)).Items.Select(item => item.Code));
        Assert.Equal(["A-1"], (await query.ListAsync(new ProductServiceFilter(null, "100%", null, 1, 10), Ct)).Items.Select(item => item.Code));
        Assert.Equal(["B_1"], (await query.ListAsync(new ProductServiceFilter("b_", null, null, 1, 10), Ct)).Items.Select(item => item.Code));
        Assert.Empty((await query.ListAsync(new ProductServiceFilter("%", null, null, 1, 10), Ct)).Items);
        Assert.Equal(["A-2"], (await query.ListAsync(new ProductServiceFilter(null, null, false, 1, 10), Ct)).Items.Select(item => item.Code));

        var page = await query.ListAsync(new ProductServiceFilter(null, null, null, 2, 2), Ct);
        Assert.Equal(3, page.TotalCount);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Documento_vinculado_volta_com_o_produto_e_a_lista_filtra_por_codigo()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        var product = Product("conta-pj", "Conta PJ");
        var linked = DocumentFor(product.Id);
        var loose = DocumentFor(null);

        await using (var write = fixture.CreateContext())
        {
            await new ProductServiceRepository(write).AddAsync(product, Ct);
            write.Documents.AddRange(linked, loose);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = fixture.CreateContext();
        var documents = new DocumentRepository(read);

        var found = await documents.FindByIdAsync(linked.Id, includeEvents: false, Ct);
        Assert.Equal("CONTA-PJ", found!.ProductService!.Code);
        Assert.Null((await documents.FindByIdAsync(loose.Id, includeEvents: false, Ct))!.ProductService);

        var filtered = await documents.ListAsync(
            new DocumentListFilter(null, null, null, null, null, null, null, 1, 10, "conta-pj"), Ct);
        Assert.Equal([linked.Id], filtered.Items.Select(item => item.Id));
        Assert.Equal("Conta PJ", filtered.Items.Single().ProductService!.Name);

        var all = await documents.ListAsync(new DocumentListFilter(null, null, null, null, null, null, null, 1, 10), Ct);
        Assert.Equal(2, all.TotalCount);
    }

    [Fact]
    public async Task Produto_com_documento_conta_como_referenciado_e_o_banco_impede_a_exclusao()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        var product = Product("conta-pj");
        await using (var write = fixture.CreateContext())
        {
            await new ProductServiceRepository(write).AddAsync(product, Ct);
            write.Documents.Add(DocumentFor(product.Id));
            await write.SaveChangesAsync(Ct);
        }

        await using var context = fixture.CreateContext();
        var repository = new ProductServiceRepository(context);

        Assert.True(await repository.IsReferencedAsync(product.Id, Ct));

        var tracked = await repository.FindByIdAsync(product.Id, Ct);
        await Assert.ThrowsAsync<DbUpdateException>(() => repository.RemoveAsync(tracked!, Ct));
    }

    [Fact]
    public async Task Produto_sem_uso_pode_ser_excluido_e_atualizado()
    {
        Assert.SkipWhen(fixture.ConnectionString is null, fixture.SkipReason ?? "sem banco");

        var product = Product("conta-pj");
        await using (var write = fixture.CreateContext())
        {
            await new ProductServiceRepository(write).AddAsync(product, Ct);
        }

        await using (var update = fixture.CreateContext())
        {
            var repository = new ProductServiceRepository(update);
            var tracked = await repository.FindByIdAsync(product.Id, Ct);
            tracked!.Update("Novo nome", false, Now.AddDays(1));
            await repository.SaveChangesAsync(Ct);
        }

        await using (var check = fixture.CreateContext())
        {
            var repository = new ProductServiceRepository(check);
            var loaded = await repository.FindByIdAsync(product.Id, Ct);
            Assert.Equal("Novo nome", loaded!.Name);
            Assert.False(loaded.Active);
            Assert.Equal(Now.AddDays(1), loaded.UpdatedAt);

            Assert.False(await repository.IsReferencedAsync(product.Id, Ct));
            await repository.RemoveAsync(loaded, Ct);
            Assert.Null(await repository.FindByIdAsync(product.Id, Ct));
        }
    }
}
