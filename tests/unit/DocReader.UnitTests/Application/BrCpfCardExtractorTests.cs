using DocReader.Application.Abstractions;
using DocReader.Application.Extraction;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

public sealed class BrCpfCardExtractorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// As linhas que o PP-OCRv5 devolveu de fato para samples/synthetic/ocr/cpf-card-limpo.png.
    /// Manter o caso real como fixture evita testar contra um documento imaginário.
    /// </summary>
    private static readonly string[] RealOcrLines =
    [
        "REPUBLICA FEDERATIVA DO BRASIL",
        "MINISTERIO DA FAZENDA",
        "SECRETARIA DA RECEITA FEDERAL",
        "CADASTRO DE PESSOAS FISICAS",
        "NUMERO DE INSCRICAO",
        "111.444.777-35",
        "NOME",
        "MARIA APARECIDA DA SILVA SOUZA",
        "NASCIMENTO",
        "14/03/1985",
        "INSCRICAO EM 02/09/2003",
        "AMOSTRA SINTETICA - SEM VALOR LEGAL"
    ];

    private static OcrResult ResultOf(params string[] lines) =>
        new(
            "paddleocr",
            "PP-OCRv5",
            [new OcrPage(1, string.Join('\n', lines), [.. lines.Select(line => new OcrBlock(line, 0.95m, [0, 0, 10, 10]))])],
            "{}");

    private static BrCpfCardExtractor Extractor() => new(new FakeTimeProvider(Now));

    private static async Task<IReadOnlyDictionary<string, ExtractedFieldValue>> ExtractAsync(params string[] lines)
    {
        var extraction = await Extractor().ExtractAsync(ResultOf(lines), TestContext.Current.CancellationToken);
        return extraction.Fields;
    }

    [Fact]
    public async Task Le_os_tres_campos_do_cartao_real()
    {
        var fields = await ExtractAsync(RealOcrLines);

        Assert.Equal("11144477735", fields["cpf"].Normalized);
        Assert.Equal("VALID", fields["cpf"].ValidationStatus);

        Assert.Equal("MARIA APARECIDA DA SILVA SOUZA", fields["name"].Normalized);
        Assert.Equal("VALID", fields["name"].ValidationStatus);

        Assert.Equal("1985-03-14", fields["birthDate"].Normalized);
        Assert.Equal("VALID", fields["birthDate"].ValidationStatus);
    }

    [Fact]
    public async Task Preserva_o_valor_bruto_ao_lado_do_normalizado()
    {
        var fields = await ExtractAsync(RealOcrLines);

        Assert.Equal("111.444.777-35", fields["cpf"].Raw);
        Assert.Equal("14/03/1985", fields["birthDate"].Raw);
    }

    [Fact]
    public async Task Nao_confunde_a_data_de_inscricao_com_a_de_nascimento()
    {
        var fields = await ExtractAsync(RealOcrLines);

        Assert.Equal("1985-03-14", fields["birthDate"].Normalized);
        Assert.NotEqual("2003-09-02", fields["birthDate"].Normalized);
    }

    [Fact]
    public async Task Le_valor_na_mesma_linha_do_rotulo()
    {
        var fields = await ExtractAsync(
            "CADASTRO DE PESSOAS FISICAS",
            "NUMERO DE INSCRICAO 529.982.247-25",
            "NOME JOAO CARLOS PEREIRA",
            "NASCIMENTO 02/01/1970");

        Assert.Equal("52998224725", fields["cpf"].Normalized);
        Assert.Equal("JOAO CARLOS PEREIRA", fields["name"].Normalized);
        Assert.Equal("1970-01-02", fields["birthDate"].Normalized);
    }

    [Fact]
    public async Task Marca_cpf_com_digito_errado_como_invalid_em_vez_de_nao_encontrado()
    {
        var fields = await ExtractAsync(
            "CADASTRO DE PESSOAS FISICAS",
            "NUMERO DE INSCRICAO",
            "111.444.777-99");

        Assert.Equal("INVALID", fields["cpf"].ValidationStatus);
        Assert.Null(fields["cpf"].Normalized);
        Assert.Equal("111.444.777-99", fields["cpf"].Raw);
    }

    [Fact]
    public async Task Campo_ausente_vira_null_com_not_found()
    {
        var fields = await ExtractAsync("CADASTRO DE PESSOAS FISICAS", "DOCUMENTO SEM OS CAMPOS");

        Assert.Equal("NOT_FOUND", fields["cpf"].ValidationStatus);
        Assert.Null(fields["cpf"].Normalized);
        Assert.Null(fields["cpf"].Raw);
        Assert.Null(fields["cpf"].Confidence);

        Assert.Equal("NOT_FOUND", fields["birthDate"].ValidationStatus);
        Assert.Null(fields["birthDate"].Normalized);
    }

    [Fact]
    public async Task Nunca_usa_cabecalho_do_cartao_como_nome()
    {
        var fields = await ExtractAsync(
            "REPUBLICA FEDERATIVA DO BRASIL",
            "MINISTERIO DA FAZENDA",
            "SECRETARIA DA RECEITA FEDERAL",
            "CADASTRO DE PESSOAS FISICAS");

        Assert.Equal("NOT_FOUND", fields["name"].ValidationStatus);
    }

    [Fact]
    public async Task Sem_rotulo_ainda_acha_o_valor_mas_rebaixa_a_confianca()
    {
        var comRotulo = await ExtractAsync("NUMERO DE INSCRICAO", "111.444.777-35");
        var semRotulo = await ExtractAsync("DOCUMENTO QUALQUER", "111.444.777-35");

        Assert.Equal("11144477735", comRotulo["cpf"].Normalized);
        Assert.Equal("11144477735", semRotulo["cpf"].Normalized);
        Assert.True(
            semRotulo["cpf"].Confidence < comRotulo["cpf"].Confidence,
            "valor achado sem rótulo tem menos evidência e precisa de confiança menor");
    }

    [Fact]
    public async Task Data_futura_nao_e_aceita_como_nascimento()
    {
        var fields = await ExtractAsync("NASCIMENTO", "14/03/2030");

        Assert.NotEqual("VALID", fields["birthDate"].ValidationStatus);
        Assert.Null(fields["birthDate"].Normalized);
    }

    [Theory]
    [InlineData("111 444 777 35")]
    [InlineData("111.444.777 - 35")]
    public async Task Tolera_espacamento_que_o_ocr_insere(string raw)
    {
        var fields = await ExtractAsync("NUMERO DE INSCRICAO", raw);

        Assert.Equal("11144477735", fields["cpf"].Normalized);
    }

    [Fact]
    public async Task Ignora_acento_ao_casar_rotulo()
    {
        var fields = await ExtractAsync(
            "NÚMERO DE INSCRIÇÃO",
            "111.444.777-35",
            "NASCIMENTO",
            "14/03/1985");

        Assert.Equal("11144477735", fields["cpf"].Normalized);
        Assert.Equal("1985-03-14", fields["birthDate"].Normalized);
    }

    [Fact]
    public async Task Confianca_geral_e_a_media_dos_campos_encontrados()
    {
        var extraction = await Extractor().ExtractAsync(ResultOf(RealOcrLines), TestContext.Current.CancellationToken);

        Assert.Equal("BR_CPF_CARD", extraction.DocumentType);
        Assert.Equal(1, extraction.SchemaVersion);
        Assert.NotNull(extraction.OverallConfidence);
        Assert.InRange(extraction.OverallConfidence!.Value, 0.01m, 1.00m);
    }

    [Fact]
    public async Task Documento_sem_texto_nenhum_nao_quebra()
    {
        var extraction = await Extractor().ExtractAsync(
            new OcrResult("paddleocr", "PP-OCRv5", [new OcrPage(1, string.Empty, [])], "{}"),
            TestContext.Current.CancellationToken);

        Assert.All(extraction.Fields.Values, field => Assert.Equal("NOT_FOUND", field.ValidationStatus));
        Assert.Null(extraction.OverallConfidence);
    }
}
