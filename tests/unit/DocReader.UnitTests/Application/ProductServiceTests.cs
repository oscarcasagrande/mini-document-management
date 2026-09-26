using DocReader.Application.Retention;
using DocReader.Application.Storage;
using DocReader.Application.Catalog;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Application.Options;
using DocReader.Domain.Catalog;
using DocReader.Domain.Documents;
using DocReader.Infrastructure.Files;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>O cadastro de produtos e serviços e o vínculo do documento no upload.</summary>
public sealed class ProductServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryProductServiceStore _store = new();
    private readonly InMemoryDocumentStore _documents = new() { Now = Now };
    private readonly InMemoryFileStorage _storage = new();

    private readonly InMemoryStorageRepositoryStore _storageRepositories = new();

    private ProductServiceService Service() =>
        new(_store, _storageRepositories, new FakeTimeProvider(Now), NullLogger<ProductServiceService>.Instance);

    private DocumentUploadService Upload() => new(
        _storage,
        _documents,
        _store,
        new RetentionService(new InMemoryRetentionPolicyStore()),
        new StorageRepositoryResolver(_storageRepositories),
        _documents,
        new SequentialProtocolGenerator(),
        new DocumentPageCounter(),
        Options.Create(new UploadOptions()),
        Options.Create(new IdempotencyOptions()),
        new FakeTimeProvider(Now),
        NullLogger<DocumentUploadService>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- dominio -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("conta-pj", "CONTA-PJ")]
    [InlineData("  Abertura_01 ", "ABERTURA_01")]
    [InlineData("A", "A")]
    [InlineData("credito.2026", "CREDITO.2026")]
    public void Codigo_valido_vira_maiusculo_e_sem_espacos(string input, string expected) =>
        Assert.Equal(expected, ProductService.NormalizeCode(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-comeca-com-hifen")]
    [InlineData("com espaco")]
    [InlineData("acentuação")]
    [InlineData("barra/perigosa")]
    public void Codigo_invalido_nao_e_normalizado(string? input) => Assert.Null(ProductService.NormalizeCode(input));

    [Fact]
    public void Codigo_com_mais_de_64_caracteres_e_invalido() =>
        Assert.Null(ProductService.NormalizeCode(new string('A', 65)));

    [Fact]
    public void Atualizar_troca_nome_e_ativo_e_registra_o_instante_sem_mexer_no_codigo()
    {
        var created = ProductService.Create(Guid.CreateVersion7(Now), "conta-pj", " Conta PJ ", true, Now);

        created.Update("Conta Empresarial", false, Now.AddHours(1));

        Assert.Equal("CONTA-PJ", created.Code);
        Assert.Equal("Conta Empresarial", created.Name);
        Assert.False(created.Active);
        Assert.Equal(Now, created.CreatedAt);
        Assert.Equal(Now.AddHours(1), created.UpdatedAt);
    }

    // ---- cadastro ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Criar_grava_o_codigo_em_maiusculo_e_devolve_o_produto()
    {
        var created = await Service().CreateAsync("conta-pj", "Abertura de conta PJ", true, null, Ct);

        Assert.Equal("CONTA-PJ", created.Code);
        Assert.True(created.Active);
        Assert.Equal(Now, created.CreatedAt);
        Assert.Single(_store.Items);
    }

    [Fact]
    public async Task Criar_com_codigo_repetido_ignorando_caixa_e_conflito()
    {
        await Service().CreateAsync("conta-pj", "A", true, null, Ct);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => Service().CreateAsync("CONTA-pj", "B", true, null, Ct));

        Assert.Equal("PRODUCT_SERVICE_CODE_EXISTS", error.ErrorCode);
    }

    [Theory]
    [InlineData("com espaco", "Nome", "INVALID_PRODUCT_SERVICE_CODE")]
    [InlineData("", "Nome", "INVALID_PRODUCT_SERVICE_CODE")]
    [InlineData("OK", "", "INVALID_PRODUCT_SERVICE_NAME")]
    [InlineData("OK", "   ", "INVALID_PRODUCT_SERVICE_NAME")]
    public async Task Criar_com_dados_invalidos_e_erro_de_validacao(string code, string name, string errorCode)
    {
        var error = await Assert.ThrowsAsync<RequestValidationException>(() => Service().CreateAsync(code, name, true, null, Ct));

        Assert.Equal(errorCode, error.ErrorCode);
    }

    [Fact]
    public async Task Nome_acima_do_limite_e_erro_de_validacao()
    {
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service().CreateAsync("OK", new string('n', ProductService.MaxNameLength + 1), true, null, Ct));
    }

    [Fact]
    public async Task Atualizar_muda_nome_e_ativo()
    {
        var created = await Service().CreateAsync("conta-pj", "A", true, null, Ct);

        var updated = await Service().UpdateAsync(created.Id, "Novo nome", false, null, Ct);

        Assert.Equal("Novo nome", updated.Name);
        Assert.False(updated.Active);
    }

    [Fact]
    public async Task Atualizar_produto_inexistente_e_not_found()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() => Service().UpdateAsync(Guid.NewGuid(), "x", true, null, Ct));

        Assert.Equal("product-service", error.Resource);
    }

    [Fact]
    public async Task Excluir_remove_o_produto_que_ninguem_usa()
    {
        var created = await Service().CreateAsync("conta-pj", "A", true, null, Ct);

        await Service().DeleteAsync(created.Id, Ct);

        Assert.Empty(_store.Items);
    }

    [Fact]
    public async Task Excluir_produto_em_uso_e_conflito_e_nada_e_removido()
    {
        var created = await Service().CreateAsync("conta-pj", "A", true, null, Ct);
        _store.IsReferenced = _ => true;

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => Service().DeleteAsync(created.Id, Ct));

        Assert.Equal("PRODUCT_SERVICE_IN_USE", error.ErrorCode);
        Assert.Single(_store.Items);
    }

    [Fact]
    public async Task Listar_filtra_por_codigo_nome_e_ativo_e_pagina()
    {
        await Service().CreateAsync("a-1", "Conta", true, null, Ct);
        await Service().CreateAsync("a-2", "Cartao", false, null, Ct);
        await Service().CreateAsync("b-1", "Conta digital", true, null, Ct);

        var byCode = await Service().ListAsync(new ProductServiceFilter("a-", null, null, 1, 10), Ct);
        var byName = await Service().ListAsync(new ProductServiceFilter(null, "conta", null, 1, 10), Ct);
        var inactive = await Service().ListAsync(new ProductServiceFilter(null, null, false, 1, 10), Ct);
        var paged = await Service().ListAsync(new ProductServiceFilter(null, null, null, 2, 2), Ct);

        Assert.Equal(["A-1", "A-2"], byCode.Items.Select(item => item.Code));
        Assert.Equal(2, byName.TotalCount);
        Assert.Equal(["A-2"], inactive.Items.Select(item => item.Code));
        Assert.Equal(["B-1"], paged.Items.Select(item => item.Code));
        Assert.Equal(2, paged.TotalPages);
    }

    // ---- vinculo no upload ---------------------------------------------------------------------------------

    private static UploadDocumentCommand Command(Stream content, string? productServiceCode) =>
        new(content, "doc.pdf", "application/pdf", null, null, UploadChannel.Api, null, productServiceCode);

    [Fact]
    public async Task Upload_com_codigo_existente_vincula_o_documento_ignorando_a_caixa()
    {
        var product = await Service().CreateAsync("conta-pj", "Conta PJ", true, null, Ct);
        await using var content = Samples.StreamOf(Samples.ThreePagePdf);

        await Upload().UploadAsync(Command(content, " conta-pj "), Ct);

        Assert.Equal(product.Id, Assert.Single(_documents.Documents).ProductServiceId);
    }

    [Fact]
    public async Task Upload_sem_codigo_nao_vincula()
    {
        await using var content = Samples.StreamOf(Samples.ThreePagePdf);

        await Upload().UploadAsync(Command(content, null), Ct);

        Assert.Null(Assert.Single(_documents.Documents).ProductServiceId);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public async Task Upload_com_codigo_em_branco_e_tratado_como_sem_codigo(string blank)
    {
        await using var content = Samples.StreamOf(Samples.ThreePagePdf);

        await Upload().UploadAsync(Command(content, blank), Ct);

        Assert.Null(Assert.Single(_documents.Documents).ProductServiceId);
    }

    [Theory]
    [InlineData("nao-existe")]
    [InlineData("codigo invalido!")]
    public async Task Upload_com_codigo_inexistente_e_422_e_nada_e_gravado(string code)
    {
        await using var content = Samples.StreamOf(Samples.ThreePagePdf);

        var error = await Assert.ThrowsAsync<UploadRejectedException>(() => Upload().UploadAsync(Command(content, code), Ct));

        Assert.Equal(UploadRejectionReason.UnprocessableContent, error.Reason);
        Assert.Equal("PRODUCT_SERVICE_NOT_FOUND", error.ErrorCode);
        Assert.Empty(_documents.Documents);
        Assert.Empty(_storage.Blobs);
    }

    [Fact]
    public async Task Upload_para_produto_inativo_e_422()
    {
        await Service().CreateAsync("conta-pj", "Conta PJ", false, null, Ct);
        await using var content = Samples.StreamOf(Samples.ThreePagePdf);

        var error = await Assert.ThrowsAsync<UploadRejectedException>(() => Upload().UploadAsync(Command(content, "conta-pj"), Ct));

        Assert.Equal(UploadRejectionReason.UnprocessableContent, error.Reason);
        Assert.Equal("PRODUCT_SERVICE_INACTIVE", error.ErrorCode);
        Assert.Empty(_documents.Documents);
    }
}
