using DocReader.Application.Abstractions;
using DocReader.Application.Extraction;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

public sealed class BrProofOfAddressExtractorTests
{
    private static async Task<IReadOnlyDictionary<string, ExtractedFieldValue>> ExtractAsync(OcrResult result) =>
        (await new BrProofOfAddressExtractor(new FakeTimeProvider(Stage3Support.Now))
            .ExtractAsync(result, TestContext.Current.CancellationToken)).Fields;

    private static Task<IReadOnlyDictionary<string, ExtractedFieldValue>> ExtractAsync(params string[] lines) =>
        ExtractAsync(Stage3Support.Of(lines));

    private static readonly string[] EnergyBill =
    [
        "COMPANHIA ENERGÉTICA EXEMPLO S.A.", "FATURA DE ENERGIA ELÉTRICA",
        "CLIENTE", "MARIA APARECIDA DA SILVA SOUZA",
        "CPF/CNPJ", "111.444.777-35",
        "ENDEREÇO", "AVENIDA DOS TESTES, 250 APTO 71",
        "BAIRRO", "VILA EXEMPLO",
        "CEP 04000-000  SÃO PAULO - SP",
        "UNIDADE CONSUMIDORA", "1234567",
        "REFERÊNCIA", "09/2026",
        "VENCIMENTO", "15/10/2026",
        "TOTAL A PAGAR", "R$ 187,45",
        "CONSUMO KWH", "312"
    ];

    [Fact]
    public async Task Fatura_de_energia_e_lida_por_rotulo()
    {
        var fields = await ExtractAsync(EnergyBill);

        Assert.Equal("MARIA APARECIDA DA SILVA SOUZA", fields["holderName"].Normalized);
        Assert.Equal("11144477735", fields["holderDocument"].Normalized);
        Assert.Equal("VALID", fields["holderDocument"].ValidationStatus);
        Assert.Equal("AVENIDA DOS TESTES, 250 APTO 71", fields["addressLine"].Normalized);
        Assert.Equal("VILA EXEMPLO", fields["neighborhood"].Normalized);
        Assert.Equal("SAO PAULO", fields["city"].Normalized);
        Assert.Equal("SP", fields["state"].Normalized);
        Assert.Equal("04000000", fields["postalCode"].Normalized);
        Assert.Equal("2026-09", fields["referenceMonth"].Normalized);
        Assert.Equal("2026-10-15", fields["dueDate"].Normalized);
        Assert.Equal("ELECTRICITY", fields["serviceType"].Normalized);
    }

    [Fact]
    public async Task Cep_na_mesma_linha_da_palavra_cep_nao_perde_confianca_por_falta_de_rotulo()
    {
        var fields = await ExtractAsync(EnergyBill);

        Assert.DoesNotContain("NO_LABEL_NEARBY", fields["postalCode"].ValidationMessages!);
        Assert.Equal(["POSTAL_CODE_VALID"], fields["postalCode"].ValidationMessages);
    }

    [Fact]
    public async Task Endereco_sem_rotulo_e_achado_pela_forma_da_linha_com_confianca_menor()
    {
        var labelled = await ExtractAsync(EnergyBill);
        var fields = await ExtractAsync("FATURA DE ENERGIA ELÉTRICA", "AVENIDA DOS TESTES, 250", "04000-000 SÃO PAULO - SP");

        Assert.Equal("AVENIDA DOS TESTES, 250", fields["addressLine"].Normalized);
        Assert.Contains("ADDRESS_BY_SHAPE", fields["addressLine"].ValidationMessages!);
        Assert.Contains("NO_LABEL_NEARBY", fields["addressLine"].ValidationMessages!);
        Assert.True(fields["addressLine"].Confidence < labelled["addressLine"].Confidence);
        Assert.Equal("04000000", fields["postalCode"].Normalized);
        Assert.Contains("NO_LABEL_NEARBY", fields["postalCode"].ValidationMessages!);
        Assert.Equal("SAO PAULO", fields["city"].Normalized);
        Assert.Equal("SP", fields["state"].Normalized);
    }

    [Fact]
    public async Task Cidade_e_uf_por_rotulo_quando_o_documento_os_separa()
    {
        var fields = await ExtractAsync("MUNICÍPIO", "Campinas", "UF", "SP", "CEP", "13000-000");

        Assert.Equal("CAMPINAS", fields["city"].Normalized);
        Assert.Equal("SP", fields["state"].Normalized);
        Assert.Equal("13000000", fields["postalCode"].Normalized);
    }

    [Fact]
    public async Task Uf_inexistente_nao_e_aceita_nem_na_forma_da_linha()
    {
        var fields = await ExtractAsync("04000-000 SAO PAULO - XX");

        Assert.Equal("NOT_FOUND", fields["state"].ValidationStatus);
        Assert.Equal("NOT_FOUND", fields["city"].ValidationStatus);
    }

    [Fact]
    public async Task Titular_pessoa_juridica_com_cnpj_alfanumerico()
    {
        var fields = await ExtractAsync("CLIENTE", "EXEMPLO SINTETICO LTDA", "CPF/CNPJ", "12.ABC.345/01DE-35");

        Assert.Equal("12ABC34501DE35", fields["holderDocument"].Normalized);
        Assert.Equal("VALID", fields["holderDocument"].ValidationStatus);
    }

    [Fact]
    public async Task Documento_do_titular_com_digito_errado_e_invalido()
    {
        var fields = await ExtractAsync("CPF/CNPJ", "111.444.777-99");

        Assert.Equal("INVALID", fields["holderDocument"].ValidationStatus);
        Assert.Null(fields["holderDocument"].Normalized);
        Assert.Equal("111.444.777-99", fields["holderDocument"].Raw);
    }

    [Fact]
    public async Task Duas_familias_de_servico_deixam_o_tipo_incerto()
    {
        var fields = await ExtractAsync("FATURA DE ENERGIA ELÉTRICA", "FATURA DE INTERNET BANDA LARGA");

        Assert.Equal("UNCERTAIN", fields["serviceType"].ValidationStatus);
        Assert.Null(fields["serviceType"].Normalized);
        Assert.Equal(["SERVICE_TYPE_AMBIGUOUS"], fields["serviceType"].ValidationMessages);
    }

    [Theory]
    [InlineData("CONTA DE ÁGUA E ESGOTO", "WATER")]
    [InlineData("GÁS NATURAL ENCANADO", "GAS")]
    [InlineData("FATURA DE INTERNET", "TELECOM")]
    [InlineData("CONSUMO 312 KWH", "ELECTRICITY")]
    public async Task Servico_por_palavra_chave(string line, string service)
    {
        var fields = await ExtractAsync(line);

        Assert.Equal(service, fields["serviceType"].Normalized);
    }

    [Fact]
    public async Task Sem_palavra_de_servico_o_tipo_e_not_found()
    {
        var fields = await ExtractAsync("CEP 04000-000", "ENDEREÇO", "RUA DAS AMOSTRAS, 10");

        Assert.Equal("NOT_FOUND", fields["serviceType"].ValidationStatus);
    }

    [Fact]
    public async Task Competencia_invalida_nao_vira_mes_de_referencia()
    {
        var fields = await ExtractAsync("REFERÊNCIA", "13/2026");

        Assert.Equal("NOT_FOUND", fields["referenceMonth"].ValidationStatus);
    }

    [Fact]
    public async Task Cep_todo_zero_nao_e_aceito()
    {
        var fields = await ExtractAsync("CEP 00000-000 SAO PAULO - SP");

        Assert.Equal("NOT_FOUND", fields["postalCode"].ValidationStatus);
    }

    [Fact]
    public async Task Coluna_a_direita_nao_contamina_o_campo_da_esquerda()
    {
        var fields = await ExtractAsync(Stage3Support.Laid(
            new("CLIENTE", 100, 250),
            new("VENCIMENTO", 660, 250),
            new("MARIA APARECIDA DA SILVA SOUZA", 100, 284, 460, 34),
            new("15/10/2026", 660, 284, 220, 34)));

        Assert.Equal("MARIA APARECIDA DA SILVA SOUZA", fields["holderName"].Normalized);
        Assert.Equal("2026-10-15", fields["dueDate"].Normalized);
    }

    [Fact]
    public async Task Ocr_vazio_devolve_tudo_not_found()
    {
        var fields = await ExtractAsync(Stage3Support.Of());

        Assert.All(fields.Values, field => Assert.Equal("NOT_FOUND", field.ValidationStatus));
    }
}
