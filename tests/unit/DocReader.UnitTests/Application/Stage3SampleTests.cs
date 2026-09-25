using DocReader.Application.Abstractions;
using DocReader.Application.Classification;
using DocReader.Application.Extraction;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>
/// Classificação e extração sobre o OCR real das amostras sintéticas da Etapa 3.
///
/// As fixtures em <c>Fixtures/ocr</c> são o que o ocr-service devolveu para cada amostra, capturado por
/// <c>scripts/capture-ocr-fixtures.py</c>; o <c>expected.json</c> vem do mesmo gerador que desenhou a
/// amostra. Se o OCR, o classificador ou um extrator mudar, é este teste que mostra o efeito em cada campo.
/// </summary>
public sealed class Stage3SampleTests
{
    private static readonly RulesDocumentClassifier Classifier = new();

    private static IDocumentExtractor ExtractorFor(string documentType)
    {
        var clock = new FakeTimeProvider(Stage3Support.Now);

        return documentType switch
        {
            BrCinExtractor.TypeName => new BrCinExtractor(clock),
            BrCnhExtractor.TypeName => new BrCnhExtractor(clock),
            BrProofOfAddressExtractor.TypeName => new BrProofOfAddressExtractor(clock),
            BrCnpjCardExtractor.TypeName => new BrCnpjCardExtractor(clock),
            BrCcmeiExtractor.TypeName => new BrCcmeiExtractor(clock),
            BrSocialContractExtractor.TypeName => new BrSocialContractExtractor(clock),
            _ => throw new ArgumentOutOfRangeException(nameof(documentType), documentType, "no extractor")
        };
    }

    public static TheoryData<string> Samples =>
    [
        "cin-frente-verso",
        "cnh",
        "cnh-vencida",
        "comprovante-residencia",
        "cartao-cnpj",
        "ccmei",
        "contrato-social"
    ];

    [Theory]
    [MemberData(nameof(Samples))]
    public void Amostra_e_classificada_como_o_tipo_esperado(string sample)
    {
        var expected = Stage3Support.Expected(sample);

        var classification = Classifier.Classify(Stage3Support.Fixture(sample));

        Assert.Equal(expected.DocumentType, classification.DocumentType);
        Assert.True(classification.Confidence >= 0.7m, $"confiança {classification.Confidence}");
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Campos_extraidos_da_amostra_batem_com_o_esperado(string sample)
    {
        var expected = Stage3Support.Expected(sample);
        var extraction = await ExtractorFor(expected.DocumentType)
            .ExtractAsync(Stage3Support.Fixture(sample), TestContext.Current.CancellationToken);

        var mismatches = new List<string>();

        foreach (var (path, want) in expected.Fields)
        {
            if (!extraction.Fields.TryGetValue(path, out var got))
            {
                mismatches.Add($"{path}: campo ausente, esperado {want.Normalized ?? "null"} ({want.Status})");
                continue;
            }

            if (got.ValidationStatus != want.Status || got.Normalized != want.Normalized)
            {
                mismatches.Add(
                    $"{path}: esperado {want.Normalized ?? "null"} ({want.Status}), " +
                    $"veio {got.Normalized ?? "null"} ({got.ValidationStatus}, raw \"{got.Raw}\")");
            }

            var missing = want.Messages.Where(code => !(got.ValidationMessages ?? []).Contains(code)).ToArray();
            if (missing.Length > 0)
            {
                mismatches.Add($"{path}: faltam os códigos {string.Join(", ", missing)}");
            }
        }

        Assert.True(mismatches.Count == 0, sample + ":\n  " + string.Join("\n  ", mismatches));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Campos_encontrados_trazem_confianca_e_evidencia(string sample)
    {
        var expected = Stage3Support.Expected(sample);
        var extraction = await ExtractorFor(expected.DocumentType)
            .ExtractAsync(Stage3Support.Fixture(sample), TestContext.Current.CancellationToken);

        Assert.NotNull(extraction.OverallConfidence);

        foreach (var (path, field) in extraction.Fields.Where(pair => pair.Value.ValidationStatus != "NOT_FOUND"))
        {
            Assert.True(field.Confidence is > 0m and <= 1m, $"{path}: confiança {field.Confidence}");
            Assert.NotNull(field.PageNumber);
            Assert.False(string.IsNullOrWhiteSpace(field.Raw), $"{path}: raw vazio");
        }
    }

    [Fact]
    public async Task Contrato_de_tres_paginas_extrai_socios_e_dados_de_paginas_diferentes()
    {
        var extraction = await ExtractorFor("BR_SOCIAL_CONTRACT")
            .ExtractAsync(Stage3Support.Fixture("contrato-social"), TestContext.Current.CancellationToken);

        Assert.Equal(3, Stage3Support.Fixture("contrato-social").Pages.Count);
        Assert.Contains("partners[0].name", extraction.Fields.Keys);
        Assert.Contains("partners[1].name", extraction.Fields.Keys);
    }
}
