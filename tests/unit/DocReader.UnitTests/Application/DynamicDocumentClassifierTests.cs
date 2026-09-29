using DocReader.Application.Classification;
using DocReader.Application.Options;
using DocReader.UnitTests.Fakes;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>
/// O classificador de produção: lê os tipos ativos do banco a cada chamada, então uma regra editada
/// aplica sem reiniciar, e um tipo desativado deixa de ser candidato.
/// </summary>
public sealed class DynamicDocumentClassifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DynamicDocumentClassifier Classifier(InMemoryDocumentTypeStore store, decimal minimumScore = 0m) =>
        new(store, Options.Create(new ClassificationOptions { MinimumScore = minimumScore }));

    [Fact]
    public async Task Classifica_com_as_regras_gravadas_no_banco_para_os_tipos_embutidos()
    {
        var store = InMemoryDocumentTypeStore.WithBuiltIns();

        var result = await Classifier(store).ClassifyAsync(
            Stage3Support.Of(
                "CADASTRO DE PESSOAS FISICAS",
                "NUMERO DE INSCRICAO",
                "MINISTERIO DA FAZENDA",
                "SECRETARIA DA RECEITA FEDERAL",
                "NASCIMENTO"),
            Ct);

        Assert.Equal("BR_CPF_CARD", result.DocumentType);
        Assert.Equal(RulesDocumentClassifier.ClassifierVersion, result.ClassifierVersion);
    }

    [Fact]
    public async Task Tipo_desativado_deixa_de_ser_reconhecido()
    {
        var store = InMemoryDocumentTypeStore.WithBuiltIns();
        var cpfCard = Assert.Single(store.Items, item => item.Code == "BR_CPF_CARD");
        cpfCard.Update(cpfCard.Name, cpfCard.SchemaJson, cpfCard.ClassificationRulesJson, cpfCard.ExtractionRulesJson, active: false, DateTimeOffset.UtcNow);

        var result = await Classifier(store).ClassifyAsync(
            Stage3Support.Of("CADASTRO DE PESSOAS FISICAS", "NUMERO DE INSCRICAO", "MINISTERIO DA FAZENDA"),
            Ct);

        Assert.Equal("UNKNOWN", result.DocumentType);
    }

    [Fact]
    public async Task Regra_editada_no_banco_aplica_na_proxima_classificacao()
    {
        var store = InMemoryDocumentTypeStore.WithBuiltIns();
        var cpfCard = Assert.Single(store.Items, item => item.Code == "BR_CPF_CARD");
        const string strictRules = """{"evidence":[{"name":"title","weight":0.3,"patterns":["CADASTRO DE PESSOAS FISICAS"]}],"threshold":0.9}""";
        cpfCard.Update(cpfCard.Name, cpfCard.SchemaJson, strictRules, cpfCard.ExtractionRulesJson, active: true, DateTimeOffset.UtcNow);

        var result = await Classifier(store).ClassifyAsync(Stage3Support.Of("CADASTRO DE PESSOAS FISICAS"), Ct);

        Assert.Equal("UNKNOWN", result.DocumentType);
    }

    [Fact]
    public async Task Diagnose_tambem_le_o_banco_e_bate_com_classify()
    {
        var store = InMemoryDocumentTypeStore.WithBuiltIns();
        var classifier = Classifier(store);

        var diagnostics = await classifier.DiagnoseAsync("CADASTRO DE PESSOAS FISICAS\nNUMERO DE INSCRICAO\nMINISTERIO DA FAZENDA", Ct);
        var classification = await classifier.ClassifyAsync(
            Stage3Support.Of("CADASTRO DE PESSOAS FISICAS", "NUMERO DE INSCRICAO", "MINISTERIO DA FAZENDA"), Ct);

        Assert.Equal(diagnostics.DocumentType, classification.DocumentType);
    }
}
