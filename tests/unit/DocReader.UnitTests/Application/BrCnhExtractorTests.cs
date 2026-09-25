using DocReader.Application.Abstractions;
using DocReader.Application.Extraction;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

public sealed class BrCnhExtractorTests
{
    private static async Task<IReadOnlyDictionary<string, ExtractedFieldValue>> ExtractAsync(OcrResult result) =>
        (await new BrCnhExtractor(new FakeTimeProvider(Stage3Support.Now))
            .ExtractAsync(result, TestContext.Current.CancellationToken)).Fields;

    private static Task<IReadOnlyDictionary<string, ExtractedFieldValue>> ExtractAsync(params string[] lines) =>
        ExtractAsync(Stage3Support.Of(lines));

    [Fact]
    public async Task Extrai_todos_os_campos_com_rotulos_numerados_da_cnh()
    {
        var fields = await ExtractAsync(
            "REPÚBLICA FEDERATIVA DO BRASIL", "CARTEIRA NACIONAL DE HABILITAÇÃO",
            "NOME", "MARIA APARECIDA DA SILVA SOUZA",
            "DOC. IDENTIDADE / ORG. EMISSOR / UF", "12.345.678-9 SSP SP",
            "CPF", "111.444.777-35",
            "3 DATA NASCIMENTO", "14/03/1985",
            "5 Nº REGISTRO", "04512345678",
            "9 CAT. HAB.", "AB",
            "1ª HABILITAÇÃO", "01/07/2004",
            "4a DATA EMISSÃO", "01/07/2024",
            "4b VALIDADE", "01/07/2029");

        Assert.Equal("MARIA APARECIDA DA SILVA SOUZA", fields["name"].Normalized);
        Assert.Equal("11144477735", fields["cpf"].Normalized);
        Assert.Equal("VALID", fields["cpf"].ValidationStatus);
        Assert.Equal("1985-03-14", fields["birthDate"].Normalized);
        Assert.Equal("04512345678", fields["registrationNumber"].Normalized);
        Assert.Equal("AB", fields["category"].Normalized);
        Assert.Equal("2004-07-01", fields["firstLicenseDate"].Normalized);
        Assert.Equal("2024-07-01", fields["issueDate"].Normalized);
        Assert.Equal("2029-07-01", fields["expirationDate"].Normalized);
    }

    [Fact]
    public async Task Documento_de_identidade_com_numero_nao_e_confundido_com_registro_ou_cpf()
    {
        var fields = await ExtractAsync(
            "DOC. IDENTIDADE / ORG. EMISSOR / UF", "12.345.678-9 SSP SP", "CPF", "111.444.777-35");

        Assert.Equal("11144477735", fields["cpf"].Normalized);
        Assert.Equal("NOT_FOUND", fields["registrationNumber"].ValidationStatus);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("b")]
    [InlineData("AB")]
    [InlineData("AE")]
    [InlineData("ACC")]
    [InlineData(" C ")]
    public async Task Categoria_valida_e_normalizada(string value)
    {
        var fields = await ExtractAsync("CAT. HAB.", value);

        Assert.Equal("VALID", fields["category"].ValidationStatus);
        Assert.Equal(value.Trim().ToUpperInvariant(), fields["category"].Normalized);
    }

    [Theory]
    [InlineData("Z")]
    [InlineData("ABCD")]
    [InlineData("12")]
    public async Task Categoria_inexistente_nao_e_aceita(string value)
    {
        var fields = await ExtractAsync("CAT. HAB.", value);

        Assert.Equal("NOT_FOUND", fields["category"].ValidationStatus);
        Assert.Null(fields["category"].Normalized);
    }

    [Theory]
    [InlineData("0451234567")]
    [InlineData("045123456789")]
    [InlineData("ABC12345678")]
    public async Task Registro_precisa_ter_exatamente_onze_digitos(string value)
    {
        var fields = await ExtractAsync("Nº REGISTRO", value);

        Assert.Equal("NOT_FOUND", fields["registrationNumber"].ValidationStatus);
    }

    [Fact]
    public async Task Cpf_invalido_na_cnh_e_reportado_como_invalido()
    {
        var fields = await ExtractAsync("CARTEIRA NACIONAL DE HABILITAÇÃO", "CPF", "111.444.777-99");

        Assert.Equal("INVALID", fields["cpf"].ValidationStatus);
        Assert.Equal(["CHECK_DIGIT_INVALID"], fields["cpf"].ValidationMessages);
    }

    [Fact]
    public async Task Primeira_habilitacao_lida_como_1a_sem_o_ordinal_ainda_casa()
    {
        var fields = await ExtractAsync("1A HABILITACAO", "01/07/2004");

        Assert.Equal("2004-07-01", fields["firstLicenseDate"].Normalized);
    }

    [Fact]
    public async Task Rotulo_e_valor_na_mesma_linha_com_dois_pontos()
    {
        var fields = await ExtractAsync("Nº Registro: 04512345678", "Validade: 01/07/2029", "Cat. Hab.: B");

        Assert.Equal("04512345678", fields["registrationNumber"].Normalized);
        Assert.Equal("2029-07-01", fields["expirationDate"].Normalized);
        Assert.Equal("B", fields["category"].Normalized);
    }

    [Fact]
    public async Task Grade_de_tres_colunas_associa_cada_valor_ao_rotulo_de_cima()
    {
        var fields = await ExtractAsync(Stage3Support.Laid(
            new("5 Nº REGISTRO", 60, 580, 240, 22),
            new("9 CAT. HAB.", 440, 580, 200, 22),
            new("1ª HABILITAÇÃO", 780, 580, 240, 22),
            new("04512345678", 60, 614, 240, 34),
            new("AB", 440, 614, 60, 34),
            new("01/07/2004", 780, 614, 200, 34)));

        Assert.Equal("04512345678", fields["registrationNumber"].Normalized);
        Assert.Equal("AB", fields["category"].Normalized);
        Assert.Equal("2004-07-01", fields["firstLicenseDate"].Normalized);
    }

    [Fact]
    public async Task Ocr_vazio_devolve_tudo_not_found()
    {
        var fields = await ExtractAsync(Stage3Support.Of());

        Assert.All(fields.Values, field => Assert.Equal("NOT_FOUND", field.ValidationStatus));
    }
}
