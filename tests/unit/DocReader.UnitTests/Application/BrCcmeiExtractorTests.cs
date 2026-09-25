using DocReader.Application.Abstractions;
using DocReader.Application.Extraction;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

public sealed class BrCcmeiExtractorTests
{
    private static async Task<IReadOnlyDictionary<string, ExtractedFieldValue>> ExtractAsync(OcrResult result) =>
        (await new BrCcmeiExtractor(new FakeTimeProvider(Stage3Support.Now))
            .ExtractAsync(result, TestContext.Current.CancellationToken)).Fields;

    private static Task<IReadOnlyDictionary<string, ExtractedFieldValue>> ExtractAsync(params string[] lines) =>
        ExtractAsync(Stage3Support.Of(lines));

    private static readonly string[] Certificate =
    [
        "CCMEI", "CERTIFICADO DA CONDIÇÃO DE MICROEMPREENDEDOR INDIVIDUAL", "Portal do Empreendedor",
        "NOME EMPRESARIAL", "MARIA APARECIDA DA SILVA SOUZA 11144477735",
        "NOME FANTASIA", "MARIA DOCES",
        "CNPJ", "04.252.011/0001-10",
        "DATA DE ABERTURA", "10/02/2020",
        "CAPITAL SOCIAL", "R$ 5.000,00",
        "OCUPAÇÃO PRINCIPAL", "10.91-1-02 - Fabricação de produtos de padaria e confeitaria",
        "ENDEREÇO COMERCIAL", "RUA DAS AMOSTRAS, 100 - CENTRO", "SAO PAULO/SP", "CEP 01000-000",
        "DADOS DO EMPRESÁRIO",
        "NOME", "MARIA APARECIDA DA SILVA SOUZA",
        "CPF", "111.444.777-35",
        "DATA DE NASCIMENTO", "14/03/1985",
        "DATA DE EMISSÃO", "01/09/2026"
    ];

    [Fact]
    public async Task Certificado_completo_e_lido_campo_a_campo()
    {
        var fields = await ExtractAsync(Certificate);

        Assert.Equal("04252011000110", fields["cnpj"].Normalized);
        Assert.Equal("VALID", fields["cnpj"].ValidationStatus);
        Assert.Equal("MARIA APARECIDA DA SILVA SOUZA 11144477735", fields["legalName"].Normalized);
        Assert.Equal("MARIA DOCES", fields["tradeName"].Normalized);
        Assert.Equal("2020-02-10", fields["openingDate"].Normalized);
        Assert.Equal("5000.00", fields["shareCapital"].Normalized);
        Assert.Equal("1091102", fields["mainActivityCode"].Normalized);
        Assert.Equal("FABRICACAO DE PRODUTOS DE PADARIA E CONFEITARIA", fields["mainActivityDescription"].Normalized);
        Assert.Equal("MARIA APARECIDA DA SILVA SOUZA", fields["holderName"].Normalized);
        Assert.Equal("11144477735", fields["holderCpf"].Normalized);
        Assert.Equal("VALID", fields["holderCpf"].ValidationStatus);
        Assert.Equal("1985-03-14", fields["holderBirthDate"].Normalized);
        Assert.Equal("2026-09-01", fields["certificateDate"].Normalized);
    }

    [Fact]
    public async Task Endereco_em_varias_linhas_e_juntado_e_cep_cidade_e_uf_saem_dele()
    {
        var fields = await ExtractAsync(Certificate);

        Assert.Equal("RUA DAS AMOSTRAS, 100 - CENTRO SAO PAULO/SP CEP 01000-000", fields["address"].Normalized);
        Assert.Equal("RUA DAS AMOSTRAS, 100 - CENTRO SAO PAULO/SP CEP 01000-000", fields["address"].Raw);
        Assert.Equal("01000000", fields["postalCode"].Normalized);
        Assert.Equal("SAO PAULO", fields["city"].Normalized);
        Assert.Equal("SP", fields["state"].Normalized);
    }

    [Fact]
    public async Task Bairro_nao_gruda_no_nome_da_cidade_quando_as_linhas_nao_tem_virgula()
    {
        var fields = await ExtractAsync("ENDEREÇO COMERCIAL", "RUA DAS AMOSTRAS, 100 - CENTRO", "CAMPINAS/SP", "CEP 13000-000");

        Assert.Equal("CAMPINAS", fields["city"].Normalized);
        Assert.Equal("SP", fields["state"].Normalized);
    }

    [Fact]
    public async Task Endereco_em_uma_linha_so_tambem_e_lido()
    {
        var fields = await ExtractAsync("Endereço comercial: RUA DAS AMOSTRAS, 100, CENTRO, SAO PAULO/SP, CEP 01000-000");

        Assert.Equal("01000000", fields["postalCode"].Normalized);
        Assert.Equal("SAO PAULO", fields["city"].Normalized);
        Assert.Equal("SP", fields["state"].Normalized);
    }

    [Fact]
    public async Task Cnpj_do_certificado_com_digito_errado_e_invalido()
    {
        var fields = await ExtractAsync("CNPJ", "04.252.011/0001-11");

        Assert.Equal("INVALID", fields["cnpj"].ValidationStatus);
        Assert.Null(fields["cnpj"].Normalized);
    }

    [Fact]
    public async Task Cpf_do_empresario_com_digito_errado_e_invalido()
    {
        var fields = await ExtractAsync("DADOS DO EMPRESÁRIO", "CPF", "111.444.777-99");

        Assert.Equal("INVALID", fields["holderCpf"].ValidationStatus);
        Assert.Equal(["CHECK_DIGIT_INVALID"], fields["holderCpf"].ValidationMessages);
    }

    [Fact]
    public async Task Capital_social_em_formato_de_reais_vira_decimal_com_ponto()
    {
        var fields = await ExtractAsync("CAPITAL SOCIAL", "R$ 12.500,50");

        Assert.Equal("12500.50", fields["shareCapital"].Normalized);
        Assert.Equal("R$ 12.500,50", fields["shareCapital"].Raw);
    }

    [Fact]
    public async Task Capital_social_ilegivel_nao_vira_valor()
    {
        var fields = await ExtractAsync("CAPITAL SOCIAL", "R$ cinco mil");

        Assert.Equal("NOT_FOUND", fields["shareCapital"].ValidationStatus);
    }

    [Fact]
    public async Task Ocr_vazio_devolve_tudo_not_found()
    {
        var fields = await ExtractAsync(Stage3Support.Of());

        Assert.All(fields.Values, field => Assert.Equal("NOT_FOUND", field.ValidationStatus));
    }

    /// <summary>
    /// Regressão do OCR real: "SAO PAULO/SP" saiu em dois blocos na mesma linha visual ("SAO" e "PAULO/SP").
    /// Sem juntar os dois, a cidade e a UF se perdiam.
    /// </summary>
    [Fact]
    public async Task Valor_partido_pelo_ocr_em_dois_blocos_na_mesma_linha_e_juntado()
    {
        var fields = await ExtractAsync(Stage3Support.Laid(
            new("ENDEREÇO COMERCIAL", 80, 800, 300, 22),
            new("RUA DAS AMOSTRAS, 100 - CENTRO", 80, 836, 520, 32),
            new("SAO", 80, 884, 70, 32),
            new("PAULO/SP", 160, 884, 180, 32),
            new("CEP 01000-000", 80, 932, 240, 32)));

        Assert.Equal("RUA DAS AMOSTRAS, 100 - CENTRO SAO PAULO/SP CEP 01000-000", fields["address"].Normalized);
        Assert.Equal("SAO PAULO", fields["city"].Normalized);
        Assert.Equal("SP", fields["state"].Normalized);
    }

    [Fact]
    public async Task Endereco_por_geometria_junta_as_tres_linhas_de_baixo_do_rotulo()
    {
        var fields = await ExtractAsync(Stage3Support.Laid(
            new("ENDEREÇO COMERCIAL", 80, 800, 300, 22),
            new("RUA DAS AMOSTRAS, 100 - CENTRO", 80, 836, 520, 32),
            new("SAO PAULO/SP", 80, 884, 260, 32),
            new("CEP 01000-000", 80, 932, 240, 32),
            new("DADOS DO EMPRESÁRIO", 80, 1040, 320, 26)));

        Assert.Equal("RUA DAS AMOSTRAS, 100 - CENTRO SAO PAULO/SP CEP 01000-000", fields["address"].Normalized);
        Assert.Equal("SP", fields["state"].Normalized);
    }
}
