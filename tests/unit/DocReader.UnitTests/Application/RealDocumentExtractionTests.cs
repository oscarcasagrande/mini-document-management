using DocReader.Application.Abstractions;
using DocReader.Application.Classification;
using DocReader.Application.Extraction;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>
/// Extração sobre o OCR real de dois exemplares de documento (uma CNH e um Registro de Identidade Civil), não
/// sobre amostras desenhadas para o extrator. As fixtures <c>cnh-real</c> e <c>ric-real</c> são o texto e as
/// coordenadas que o PP-OCRv5 devolveu, com nomes, CPF e números trocados por valores fictícios.
///
/// Cada caso abaixo é uma variação que só o documento real trouxe e que os extratores, calibrados nas amostras
/// sintéticas, não cobriam: rótulo com numerador duplo ou colado, bilíngue, com erro de letra, data com espaço,
/// nome em duas linhas, MRZ partida em blocos. Se um deles regredir, o caso real volta a falhar aqui.
/// </summary>
public sealed class RealDocumentExtractionTests
{
    private static readonly FakeTimeProvider Clock = new(Stage3Support.Now);

    private static async Task<IReadOnlyDictionary<string, ExtractedFieldValue>> ExtractCnh() =>
        (await new BrCnhExtractor(Clock).ExtractAsync(Stage3Support.Fixture("cnh-real"), CancellationToken.None)).Fields;

    private static async Task<IReadOnlyDictionary<string, ExtractedFieldValue>> ExtractRic() =>
        (await new BrCinExtractor(Clock).ExtractAsync(Stage3Support.Fixture("ric-real"), CancellationToken.None)).Fields;

    [Fact]
    public void Cnh_real_e_ric_real_sao_classificados()
    {
        var classifier = new RulesDocumentClassifier();

        Assert.Equal("BR_CNH", classifier.Classify(Stage3Support.Fixture("cnh-real")).DocumentType);
        Assert.Equal("BR_CIN", classifier.Classify(Stage3Support.Fixture("ric-real")).DocumentType);
    }

    // ---- CNH ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cnh_nome_sob_rotulo_com_numerador_duplo()
    {
        // O OCR leu "2 & 1 NOME E SOBRENOME" como "2e 1 NOME E SOBRENOME".
        var name = (await ExtractCnh())["name"];

        Assert.Equal("VALID", name.ValidationStatus);
        Assert.Equal("NOME SOCIAL TESTE CENTO E DEZ", name.Normalized);
    }

    [Fact]
    public async Task Cnh_registro_sob_rotulo_colado_com_numerador_e_grau()
    {
        // "5 Nº REGISTRO" saiu como "5N°REGISTRO", sem espaço nenhum.
        var registration = (await ExtractCnh())["registrationNumber"];

        Assert.Equal("VALID", registration.ValidationStatus);
        Assert.Equal("04512345621", registration.Normalized);
        Assert.Contains("CHECK_DIGIT_VALID", registration.ValidationMessages!);
    }

    [Fact]
    public async Task Cnh_primeira_habilitacao_sob_rotulo_com_indicador_ordinal()
    {
        // "1ª HABILITAÇÃO" saiu como "1° HABILITAÇÃO".
        var first = (await ExtractCnh())["firstLicenseDate"];

        Assert.Equal("VALID", first.ValidationStatus);
        Assert.Equal("2022-05-24", first.Normalized);
    }

    [Fact]
    public async Task Cnh_campos_que_ja_funcionavam_continuam()
    {
        var fields = await ExtractCnh();

        Assert.Equal("1981-09-19", fields["birthDate"].Normalized);
        Assert.Equal("11144477735", fields["cpf"].Normalized);
        Assert.Equal("2022-05-24", fields["issueDate"].Normalized);
        Assert.Equal("2023-05-23", fields["expirationDate"].Normalized);
        Assert.Contains("DOCUMENT_EXPIRED", fields["expirationDate"].ValidationMessages!);
    }

    [Fact]
    public async Task Cnh_categoria_nao_e_inventada_quando_o_ocr_nao_leu_a_letra()
    {
        // A letra da categoria está num quadro pequeno e o OCR não devolveu bloco nenhum ali: sem evidência,
        // o campo é NOT_FOUND, nunca um valor suposto (RF-011).
        var category = (await ExtractCnh())["category"];

        Assert.Equal("NOT_FOUND", category.ValidationStatus);
        Assert.Null(category.Normalized);
    }

    // ---- RIC ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ric_nome_com_rotulo_bilingue_e_valor_em_duas_linhas()
    {
        // "NOME/NAME", e o nome vem partido: "MARINA" e, na linha de baixo, "DE SOUZA LIMA".
        var name = (await ExtractRic())["name"];

        Assert.Equal("VALID", name.ValidationStatus);
        Assert.Equal("MARINA DE SOUZA LIMA", name.Normalized);
    }

    [Fact]
    public async Task Ric_datas_impressas_com_espaco_no_lugar_da_barra()
    {
        var fields = await ExtractRic();

        Assert.Equal("1975-07-12", fields["birthDate"].Normalized);
        Assert.Equal("2015-10-21", fields["expirationDate"].Normalized);
        Assert.Contains("DOCUMENT_EXPIRED", fields["expirationDate"].ValidationMessages!);
    }

    [Fact]
    public async Task Ric_data_de_expedicao_com_rotulo_no_lugar_do_de()
    {
        // "DATA DA EXPEDIÇÃO" e não "DATA DE EXPEDIÇÃO".
        var issue = (await ExtractRic())["issueDate"];

        Assert.Equal("VALID", issue.ValidationStatus);
        Assert.Equal("2010-08-20", issue.Normalized);
    }

    [Fact]
    public async Task Ric_rg_sob_rotulo_bilingue_rg_barra_uf()
    {
        var rg = (await ExtractRic())["rg"];

        Assert.Equal("VALID", rg.ValidationStatus);
        Assert.Equal("301234567", rg.Normalized);
    }

    [Fact]
    public async Task Ric_filiacao_com_erro_de_letra_no_rotulo()
    {
        // O OCR leu "FILIAÇÃO" como "FILUAÇÃO".
        var fields = await ExtractRic();

        Assert.Equal("UNCERTAIN", fields["fatherName"].ValidationStatus);
        Assert.Equal("LUIS SOUZA LIMA", fields["fatherName"].Normalized);
        Assert.Equal("UNCERTAIN", fields["motherName"].ValidationStatus);
        Assert.Equal("ANA MARIA PAULA SOUZA LIMA", fields["motherName"].Normalized);
        Assert.Contains("FILIATION_ORDER_ASSUMED", fields["fatherName"].ValidationMessages!);
    }

    [Fact]
    public async Task Ric_mrz_com_a_primeira_linha_partida_em_tres_blocos()
    {
        var mrz = (await ExtractRic())["mrz"];

        // A primeira linha chega com 29 caracteres: o OCR perdeu um "<" do preenchimento, que é reposto.
        Assert.NotEqual("NOT_FOUND", mrz.ValidationStatus);
        Assert.DoesNotContain("MRZ_LENGTH_UNEXPECTED", mrz.ValidationMessages ?? []);
        Assert.Equal(3, mrz.Normalized!.Split('\n').Length);
        Assert.All(mrz.Normalized.Split('\n'), line => Assert.Equal(30, line.Length));
    }

    [Fact]
    public async Task Ric_cpf_ficticio_reprovado_no_digito_e_reportado_como_invalido()
    {
        var cpf = (await ExtractRic())["cpf"];

        Assert.Equal("INVALID", cpf.ValidationStatus);
        Assert.Equal("123.456.789-39", cpf.Raw);
    }

    [Fact]
    public async Task Ric_naturalidade_continua()
    {
        var place = (await ExtractRic())["birthPlace"];

        Assert.Equal("VALID", place.ValidationStatus);
        Assert.Equal("UTOPIA/UT", place.Normalized);
    }
}
