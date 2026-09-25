using System.Text.Json;
using DocReader.Application.Abstractions;
using DocReader.Application.Classification;
using Xunit;

namespace DocReader.UnitTests.Application;

public sealed class RulesDocumentClassifierTests
{
    private static OcrResult ResultOf(params string[] lines) =>
        new(
            "paddleocr",
            "PP-OCRv5",
            [new OcrPage(1, string.Join('\n', lines), [.. lines.Select(line => new OcrBlock(line, 0.95m, []))])],
            "{}");

    private static readonly RulesDocumentClassifier Classifier = new();

    [Fact]
    public void Cartao_de_cpf_completo_e_identificado_com_confianca_maxima()
    {
        var result = Classifier.Classify(ResultOf(
            "REPUBLICA FEDERATIVA DO BRASIL",
            "MINISTERIO DA FAZENDA",
            "SECRETARIA DA RECEITA FEDERAL",
            "CADASTRO DE PESSOAS FISICAS",
            "NUMERO DE INSCRICAO",
            "NASCIMENTO"));

        Assert.Equal("BR_CPF_CARD", result.DocumentType);
        Assert.Equal(1.0m, result.Confidence);
        Assert.Equal(5, result.Signals.Count);
        Assert.Equal(RulesDocumentClassifier.ClassifierVersion, result.ClassifierVersion);
        Assert.True(result.IsKnown);
    }

    [Fact]
    public void Acento_e_caixa_do_ocr_nao_atrapalham()
    {
        var result = Classifier.Classify(ResultOf("Cadastro de Pessoas Físicas", "Número de Inscrição"));

        Assert.Equal("BR_CPF_CARD", result.DocumentType);
    }

    [Fact]
    public void Um_sinal_opcional_basta_para_atingir_o_limiar()
    {
        var result = Classifier.Classify(ResultOf("CADASTRO DE PESSOAS FISICAS", "NASCIMENTO"));

        Assert.Equal("BR_CPF_CARD", result.DocumentType);
        Assert.Equal(0.7m, result.Confidence);
    }

    [Fact]
    public void So_o_sinal_obrigatorio_nao_basta_e_devolve_unknown()
    {
        var result = Classifier.Classify(ResultOf("CADASTRO DE PESSOAS FISICAS"));

        Assert.Equal("UNKNOWN", result.DocumentType);
        Assert.Null(result.Confidence);
        Assert.False(result.IsKnown);
    }

    [Fact]
    public void Sinal_negativo_descarta_o_tipo_mesmo_com_o_resto_presente()
    {
        var result = Classifier.Classify(ResultOf(
            "CADASTRO DE PESSOAS FISICAS",
            "NUMERO DE INSCRICAO",
            "MINISTERIO DA FAZENDA",
            "CADASTRO NACIONAL DA PESSOA JURIDICA"));

        Assert.Equal("UNKNOWN", result.DocumentType);
    }

    [Fact]
    public void Texto_vazio_e_unknown()
    {
        Assert.Equal("UNKNOWN", Classifier.Classify(ResultOf()).DocumentType);
    }

    [Fact]
    public void Perfil_no_codigo_espelha_os_sinais_do_schema_json()
    {
        var schemaPath = Path.Combine(RepositoryRoot(), "schemas", "documents", "BR_CPF_CARD.v1.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));

        var classification = schema.RootElement
            .GetProperty("x-docreader")
            .GetProperty("classification");

        static string[] Read(JsonElement parent, string name) =>
            [.. parent.GetProperty(name).EnumerateArray().Select(item => item.GetString()!)];

        var profile = DocumentTypeProfile.BrCpfCard;

        Assert.Equal(Read(classification, "requiredSignals").Order(), profile.RequiredSignals.Order());
        Assert.Equal(Read(classification, "optionalSignals").Order(), profile.OptionalSignals.Order());
        Assert.Equal(Read(classification, "negativeSignals").Order(), profile.NegativeSignals.Order());
        Assert.Equal(classification.GetProperty("threshold").GetDecimal(), profile.Threshold);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocReader.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("DocReader.slnx not found above the test binaries.");
    }
}
