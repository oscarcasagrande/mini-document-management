using DocReader.Application.Catalog;
using DocReader.Application.Classification;
using DocReader.Application.Errors;
using DocReader.Domain.Catalog;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>
/// O cadastro de tipos documentais (Configuração Dinâmica): os sete tipos embutidos migrados do código,
/// a validação das regras de classificação e a proteção contra exclusão do que está em uso.
/// </summary>
public sealed class DocumentTypeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private const string ValidSchema = """{"type":"object"}""";
    private const string ValidRules = """{"evidence":[{"name":"title","weight":0.6,"patterns":["ALFA"]}],"threshold":0.6}""";
    private const string ValidExtraction = "{}";

    private readonly InMemoryDocumentTypeStore _store = InMemoryDocumentTypeStore.WithBuiltIns();

    private DocumentTypeService Service() =>
        new(_store, new FakeTimeProvider(Now), NullLogger<DocumentTypeService>.Instance);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- dominio -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("br_novo_tipo", "BR_NOVO_TIPO")]
    [InlineData("  Custom.01 ", "CUSTOM.01")]
    public void Codigo_valido_vira_maiusculo_e_sem_espacos(string input, string expected) =>
        Assert.Equal(expected, DocumentType.NormalizeCode(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("com espaco")]
    [InlineData("acentuação")]
    public void Codigo_invalido_nao_e_normalizado(string? input) => Assert.Null(DocumentType.NormalizeCode(input));

    [Theory]
    [InlineData("{\"a\":1}", true)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Json_bem_formado_e_reconhecido(string? json, bool expected) =>
        Assert.Equal(expected, DocumentType.IsWellFormedJson(json));

    // ---- cadastro --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Criar_grava_o_codigo_em_maiusculo_e_devolve_o_tipo()
    {
        var created = await Service().CreateAsync("br_custom", "Tipo customizado", ValidSchema, ValidRules, ValidExtraction, true, Ct);

        Assert.Equal("BR_CUSTOM", created.Code);
        Assert.True(created.Active);
        Assert.False(created.IsBuiltIn);
        Assert.Equal(Now, created.CreatedAt);
    }

    [Fact]
    public async Task Criar_com_codigo_repetido_ignorando_caixa_e_conflito()
    {
        await Service().CreateAsync("br_custom", "A", ValidSchema, ValidRules, ValidExtraction, true, Ct);

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            Service().CreateAsync("BR_custom", "B", ValidSchema, ValidRules, ValidExtraction, true, Ct));

        Assert.Equal("DOCUMENT_TYPE_CODE_EXISTS", error.ErrorCode);
    }

    [Theory]
    [InlineData("com espaco", "Nome", ValidSchema, ValidRules, ValidExtraction, "INVALID_DOCUMENT_TYPE_CODE")]
    [InlineData("OK", "", ValidSchema, ValidRules, ValidExtraction, "INVALID_DOCUMENT_TYPE_NAME")]
    [InlineData("OK", "Nome", "not json", ValidRules, ValidExtraction, "INVALID_DOCUMENT_TYPE_SCHEMA")]
    [InlineData("OK", "Nome", ValidSchema, "not json", ValidExtraction, "INVALID_DOCUMENT_TYPE_CLASSIFICATION_RULES")]
    [InlineData("OK", "Nome", ValidSchema, ValidRules, "not json", "INVALID_DOCUMENT_TYPE_EXTRACTION_RULES")]
    public async Task Criar_com_dados_invalidos_e_erro_de_validacao(
        string code, string name, string schema, string rules, string extraction, string errorCode)
    {
        var error = await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service().CreateAsync(code, name, schema, rules, extraction, true, Ct));

        Assert.Equal(errorCode, error.ErrorCode);
    }

    [Theory]
    [InlineData("""{"threshold":0.6}""")]
    [InlineData("""{"evidence":[]}""")]
    [InlineData("""{"evidence":[{"name":"title","weight":0.6,"patterns":["A"]}],"threshold":1.5}""")]
    public async Task Regras_de_classificacao_sem_forma_usavel_sao_erro_de_validacao(string rules)
    {
        var error = await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service().CreateAsync("OK", "Nome", ValidSchema, rules, ValidExtraction, true, Ct));

        Assert.Equal("INVALID_DOCUMENT_TYPE_CLASSIFICATION_RULES", error.ErrorCode);
    }

    [Fact]
    public async Task Atualizar_muda_regras_sem_mexer_no_codigo()
    {
        var created = await Service().CreateAsync("br_custom", "A", ValidSchema, ValidRules, ValidExtraction, true, Ct);
        const string newRules = """{"evidence":[{"name":"title","weight":0.9,"patterns":["BETA"]}],"threshold":0.5}""";

        var updated = await Service().UpdateAsync(created.Id, "Novo nome", ValidSchema, newRules, ValidExtraction, false, Ct);

        Assert.Equal("BR_CUSTOM", updated.Code);
        Assert.Equal("Novo nome", updated.Name);
        Assert.Equal(newRules, updated.ClassificationRulesJson);
        Assert.False(updated.Active);
    }

    [Fact]
    public async Task Atualizar_tipo_inexistente_e_not_found()
    {
        var error = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            Service().UpdateAsync(Guid.NewGuid(), "x", ValidSchema, ValidRules, ValidExtraction, true, Ct));

        Assert.Equal("document-type", error.Resource);
    }

    // ---- exclusao --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Excluir_remove_o_tipo_customizado_que_ninguem_usa()
    {
        var created = await Service().CreateAsync("br_custom", "A", ValidSchema, ValidRules, ValidExtraction, true, Ct);

        await Service().DeleteAsync(created.Id, Ct);

        Assert.DoesNotContain(_store.Items, item => item.Id == created.Id);
    }

    [Fact]
    public async Task Excluir_tipo_em_uso_e_conflito_e_nada_e_removido()
    {
        var created = await Service().CreateAsync("br_custom", "A", ValidSchema, ValidRules, ValidExtraction, true, Ct);
        _store.IsReferenced = _ => true;

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => Service().DeleteAsync(created.Id, Ct));

        Assert.Equal("DOCUMENT_TYPE_IN_USE", error.ErrorCode);
        Assert.Contains(_store.Items, item => item.Id == created.Id);
    }

    [Fact]
    public async Task Excluir_um_tipo_embutido_e_conflito()
    {
        var builtIn = Assert.Single(_store.Items, item => item.Code == "BR_CPF_CARD");

        var error = await Assert.ThrowsAsync<ResourceConflictException>(() => Service().DeleteAsync(builtIn.Id, Ct));

        Assert.Equal("DOCUMENT_TYPE_BUILT_IN_PROTECTED", error.ErrorCode);
        Assert.Contains(_store.Items, item => item.Id == builtIn.Id);
    }

    [Fact]
    public async Task Desativar_um_tipo_embutido_e_permitido()
    {
        var builtIn = Assert.Single(_store.Items, item => item.Code == "BR_CIN");

        var updated = await Service().UpdateAsync(
            builtIn.Id, builtIn.Name, builtIn.SchemaJson, builtIn.ClassificationRulesJson, builtIn.ExtractionRulesJson, active: false, Ct);

        Assert.False(updated.Active);
        Assert.True(updated.IsBuiltIn);
    }

    // ---- listagem --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Listar_filtra_por_codigo_e_pagina()
    {
        var page = await Service().ListAsync(new DocumentTypeFilter("BR_C", null, true, 1, 3), Ct);

        Assert.True(page.TotalCount >= 3);
        Assert.All(page.Items, item => Assert.StartsWith("BR_C", item.Code, StringComparison.Ordinal));
    }
}
