using DocReader.Application.Abstractions;
using DocReader.Application.Extraction;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

public sealed class BrCnpjCardExtractorTests
{
    private static async Task<IReadOnlyDictionary<string, ExtractedFieldValue>> ExtractAsync(OcrResult result) =>
        (await new BrCnpjCardExtractor(new FakeTimeProvider(Stage3Support.Now))
            .ExtractAsync(result, TestContext.Current.CancellationToken)).Fields;

    private static Task<IReadOnlyDictionary<string, ExtractedFieldValue>> ExtractAsync(params string[] lines) =>
        ExtractAsync(Stage3Support.Of(lines));

    private static readonly string[] Card =
    [
        "REPÚBLICA FEDERATIVA DO BRASIL", "CADASTRO NACIONAL DA PESSOA JURÍDICA",
        "COMPROVANTE DE INSCRIÇÃO E DE SITUAÇÃO CADASTRAL",
        "NÚMERO DE INSCRIÇÃO", "12.ABC.345/01DE-35  MATRIZ",
        "DATA DE ABERTURA", "01/03/2015",
        "NOME EMPRESARIAL", "EXEMPLO SINTETICO LTDA",
        "TÍTULO DO ESTABELECIMENTO (NOME DE FANTASIA)", "********",
        "CÓDIGO E DESCRIÇÃO DA ATIVIDADE ECONÔMICA PRINCIPAL", "62.01-5-01 - Desenvolvimento de programas de computador sob encomenda",
        "CÓDIGO E DESCRIÇÃO DA NATUREZA JURÍDICA", "206-2 - Sociedade Empresária Limitada",
        "LOGRADOURO", "RUA DAS AMOSTRAS",
        "NÚMERO", "1000",
        "COMPLEMENTO", "SALA 12",
        "CEP", "01.000-000",
        "BAIRRO/DISTRITO", "CENTRO",
        "MUNICÍPIO", "SAO PAULO",
        "UF", "SP",
        "SITUAÇÃO CADASTRAL", "ATIVA",
        "DATA DA SITUAÇÃO CADASTRAL", "01/03/2015"
    ];

    [Fact]
    public async Task Cartao_completo_e_lido_campo_a_campo()
    {
        var fields = await ExtractAsync(Card);

        Assert.Equal("12ABC34501DE35", fields["cnpj"].Normalized);
        Assert.Equal("VALID", fields["cnpj"].ValidationStatus);
        Assert.Equal(["CHECK_DIGIT_VALID"], fields["cnpj"].ValidationMessages);
        Assert.Equal("2015-03-01", fields["openingDate"].Normalized);
        Assert.Equal("EXEMPLO SINTETICO LTDA", fields["legalName"].Normalized);
        Assert.Equal("6201501", fields["mainActivityCode"].Normalized);
        Assert.Equal("62.01-5-01", fields["mainActivityCode"].Raw);
        Assert.Equal("DESENVOLVIMENTO DE PROGRAMAS DE COMPUTADOR SOB ENCOMENDA", fields["mainActivityDescription"].Normalized);
        Assert.Equal("206-2 - SOCIEDADE EMPRESARIA LIMITADA", fields["legalNature"].Normalized);
        Assert.Equal("RUA DAS AMOSTRAS", fields["street"].Normalized);
        Assert.Equal("1000", fields["number"].Normalized);
        Assert.Equal("SALA 12", fields["complement"].Normalized);
        Assert.Equal("01000000", fields["postalCode"].Normalized);
        Assert.Equal("CENTRO", fields["neighborhood"].Normalized);
        Assert.Equal("SAO PAULO", fields["city"].Normalized);
        Assert.Equal("SP", fields["state"].Normalized);
        Assert.Equal("ATIVA", fields["registrationStatus"].Normalized);
        Assert.Equal("2015-03-01", fields["registrationStatusDate"].Normalized);
    }

    [Fact]
    public async Task Nome_de_fantasia_com_asteriscos_e_not_found_porque_o_documento_nao_tem_nome()
    {
        var fields = await ExtractAsync(Card);

        Assert.Equal("NOT_FOUND", fields["tradeName"].ValidationStatus);
        Assert.Null(fields["tradeName"].Normalized);
    }

    [Fact]
    public async Task Nome_de_fantasia_preenchido_e_lido()
    {
        var lines = Card.ToArray();
        lines[Array.IndexOf(lines, "********")] = "EXEMPLO TECH";

        var fields = await ExtractAsync(lines);

        Assert.Equal("EXEMPLO TECH", fields["tradeName"].Normalized);
    }

    [Theory]
    [InlineData("04.252.011/0001-10", "04252011000110")]
    [InlineData("12.abc.345/01de-35", "12ABC34501DE35")]
    [InlineData("12.ABC.345/01DE-35 MATRIZ", "12ABC34501DE35")]
    public async Task Cnpj_numerico_legado_e_alfanumerico_em_qualquer_caixa_sao_validos(string value, string normalized)
    {
        var fields = await ExtractAsync("NÚMERO DE INSCRIÇÃO", value);

        Assert.Equal("VALID", fields["cnpj"].ValidationStatus);
        Assert.Equal(normalized, fields["cnpj"].Normalized);
    }

    [Fact]
    public async Task Cnpj_com_digito_errado_e_invalido_e_guarda_o_valor_lido()
    {
        var fields = await ExtractAsync("NÚMERO DE INSCRIÇÃO", "12.ABC.345/01DE-36");

        Assert.Equal("INVALID", fields["cnpj"].ValidationStatus);
        Assert.Null(fields["cnpj"].Normalized);
        Assert.Equal("12.ABC.345/01DE-36", fields["cnpj"].Raw);
        Assert.Equal(["CHECK_DIGIT_INVALID"], fields["cnpj"].ValidationMessages);
    }

    [Fact]
    public async Task Cnpj_de_doze_posicoes_iguais_e_rejeitado_mesmo_com_modulo_fechando()
    {
        var fields = await ExtractAsync("NÚMERO DE INSCRIÇÃO", "AA.AAA.AAA/AAAA-45");

        Assert.Equal("INVALID", fields["cnpj"].ValidationStatus);
        Assert.Null(fields["cnpj"].Normalized);
    }

    [Fact]
    public async Task Cnpj_com_digito_verificador_nao_numerico_nao_e_reconhecido()
    {
        var fields = await ExtractAsync("NÚMERO DE INSCRIÇÃO", "12.ABC.345/01DE-3X");

        Assert.Equal("NOT_FOUND", fields["cnpj"].ValidationStatus);
    }

    [Fact]
    public async Task Cnpj_sem_rotulo_perto_e_achado_no_documento_com_confianca_menor()
    {
        var labelled = await ExtractAsync("NÚMERO DE INSCRIÇÃO", "04.252.011/0001-10");
        var fields = await ExtractAsync("Empresa cadastrada sob o CNPJ 04.252.011/0001-10 desde 2020");

        Assert.Equal("04252011000110", fields["cnpj"].Normalized);
        Assert.Contains("NO_LABEL_NEARBY", fields["cnpj"].ValidationMessages!);
        Assert.True(fields["cnpj"].Confidence < labelled["cnpj"].Confidence);
    }

    [Fact]
    public async Task Atividade_sem_codigo_devolve_so_a_descricao()
    {
        var fields = await ExtractAsync("ATIVIDADE ECONOMICA PRINCIPAL", "Desenvolvimento de programas de computador");

        Assert.Equal("NOT_FOUND", fields["mainActivityCode"].ValidationStatus);
        Assert.Equal("DESENVOLVIMENTO DE PROGRAMAS DE COMPUTADOR", fields["mainActivityDescription"].Normalized);
    }

    [Fact]
    public async Task Rotulo_e_valor_na_mesma_linha_do_ocr_que_juntou_os_dois()
    {
        var fields = await ExtractAsync(
            "NÚMERO DE INSCRIÇÃO 04.252.011/0001-10 MATRIZ",
            "DATA DE ABERTURA 10/02/2020",
            "NOME EMPRESARIAL PADARIA EXEMPLO ME");

        Assert.Equal("04252011000110", fields["cnpj"].Normalized);
        Assert.Equal("2020-02-10", fields["openingDate"].Normalized);
        Assert.Equal("PADARIA EXEMPLO ME", fields["legalName"].Normalized);
    }

    [Fact]
    public async Task Data_de_abertura_futura_e_invalida()
    {
        var fields = await ExtractAsync("DATA DE ABERTURA", "01/03/2031");

        Assert.Equal("INVALID", fields["openingDate"].ValidationStatus);
    }

    [Fact]
    public async Task Cep_e_uf_invalidos_nao_sao_aceitos()
    {
        var fields = await ExtractAsync("CEP", "00.000-000", "UF", "ZZ");

        Assert.Equal("NOT_FOUND", fields["postalCode"].ValidationStatus);
        Assert.Equal("NOT_FOUND", fields["state"].ValidationStatus);
    }

    [Fact]
    public async Task Grade_de_caixas_associa_o_valor_ao_rotulo_da_propria_caixa()
    {
        var fields = await ExtractAsync(Stage3Support.Laid(
            new("NÚMERO DE INSCRIÇÃO", 72, 238, 300, 20),
            new("DATA DE ABERTURA", 632, 238, 220, 20),
            new("12.ABC.345/01DE-35  MATRIZ", 72, 272, 420, 30),
            new("01/03/2015", 632, 272, 200, 30),
            new("NOME EMPRESARIAL", 72, 348, 240, 20),
            new("EXEMPLO SINTETICO LTDA", 72, 382, 400, 30)));

        Assert.Equal("12ABC34501DE35", fields["cnpj"].Normalized);
        Assert.Equal("2015-03-01", fields["openingDate"].Normalized);
        Assert.Equal("EXEMPLO SINTETICO LTDA", fields["legalName"].Normalized);
    }

    [Fact]
    public async Task Ocr_vazio_devolve_tudo_not_found()
    {
        var fields = await ExtractAsync(Stage3Support.Of());

        Assert.All(fields.Values, field => Assert.Equal("NOT_FOUND", field.ValidationStatus));
    }
}
