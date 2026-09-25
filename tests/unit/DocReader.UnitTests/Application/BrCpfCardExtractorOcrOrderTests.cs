using DocReader.Application.Abstractions;
using DocReader.Application.Extraction;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>
/// Regression for a bug found when the real OCR ran: PP-OCRv5 does not always return the lines of the
/// CPF card in the order they were drawn. On cpf-card-limpo.png it returns the registration date
/// ("INSCRICAO EM 02/09/2003") between the NASCIMENTO label and the birth date, and the extractor
/// used to read the registration date as the birth date.
/// </summary>
public sealed class BrCpfCardExtractorOcrOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Order returned by the OCR for samples/synthetic/ocr/cpf-card-limpo.png.</summary>
    private static readonly string[] CleanCardAsReturnedByTheOcr =
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
        "INSCRICAO EM 02/09/2003",
        "14/03/1985",
        "AMOSTRA SINTETICA - SEM VALOR LEGAL"
    ];

    private static async Task<IReadOnlyDictionary<string, ExtractedFieldValue>> ExtractAsync(params string[] lines)
    {
        var result = new OcrResult(
            "paddleocr",
            "PP-OCRv5",
            [new OcrPage(1, string.Join('\n', lines), [.. lines.Select(line => new OcrBlock(line, 0.95m, [0, 0, 10, 10]))])],
            "{}");

        var extraction = await new BrCpfCardExtractor(new FakeTimeProvider(Now))
            .ExtractAsync(result, TestContext.Current.CancellationToken);

        return extraction.Fields;
    }

    [Fact]
    public async Task Data_de_inscricao_entre_o_rotulo_e_o_valor_nao_vira_nascimento()
    {
        var fields = await ExtractAsync(CleanCardAsReturnedByTheOcr);

        Assert.Equal("1985-03-14", fields["birthDate"].Normalized);
        Assert.Equal("14/03/1985", fields["birthDate"].Raw);
        Assert.Equal("VALID", fields["birthDate"].ValidationStatus);
    }

    [Fact]
    public async Task Sozinha_a_data_de_inscricao_nao_e_aceita_como_nascimento()
    {
        var fields = await ExtractAsync("CADASTRO DE PESSOAS FISICAS", "NASCIMENTO", "INSCRICAO EM 02/09/2003");

        Assert.Equal("NOT_FOUND", fields["birthDate"].ValidationStatus);
        Assert.Null(fields["birthDate"].Normalized);
    }

    [Fact]
    public async Task Data_de_inscricao_com_rotulo_por_extenso_tambem_e_ignorada()
    {
        var fields = await ExtractAsync("NASCIMENTO", "DATA DE INSCRICAO 02/09/2003", "14/03/1985");

        Assert.Equal("1985-03-14", fields["birthDate"].Normalized);
    }

    [Fact]
    public async Task Cpf_valido_vem_com_o_codigo_de_digito_verificador_conferido()
    {
        var fields = await ExtractAsync(CleanCardAsReturnedByTheOcr);

        Assert.Equal(["CHECK_DIGIT_VALID"], fields["cpf"].ValidationMessages);
        Assert.Equal(["DATE_VALID"], fields["birthDate"].ValidationMessages);
    }

    [Fact]
    public async Task Cpf_com_digito_errado_vem_com_o_codigo_de_digito_invalido()
    {
        var fields = await ExtractAsync("CADASTRO DE PESSOAS FISICAS", "NUMERO DE INSCRICAO", "111.444.777-99");

        Assert.Equal("INVALID", fields["cpf"].ValidationStatus);
        Assert.Contains("CHECK_DIGIT_INVALID", fields["cpf"].ValidationMessages!);
    }

    [Fact]
    public async Task Cpf_invalido_ao_lado_do_rotulo_nao_diz_que_faltou_rotulo()
    {
        var fields = await ExtractAsync("CADASTRO DE PESSOAS FISICAS", "NUMERO DE INSCRICAO", "111.444.777-99");

        Assert.Equal(["CHECK_DIGIT_INVALID"], fields["cpf"].ValidationMessages);
    }

    [Fact]
    public async Task Cpf_invalido_sem_rotulo_carrega_os_dois_avisos()
    {
        var fields = await ExtractAsync("DOCUMENTO QUALQUER", "111.444.777-99");

        Assert.Contains("CHECK_DIGIT_INVALID", fields["cpf"].ValidationMessages!);
        Assert.Contains("NO_LABEL_NEARBY", fields["cpf"].ValidationMessages!);
    }

    [Fact]
    public async Task Valor_sem_rotulo_carrega_o_aviso_que_explica_a_confianca_menor()
    {
        var fields = await ExtractAsync("DOCUMENTO QUALQUER", "111.444.777-35");

        Assert.Contains("NO_LABEL_NEARBY", fields["cpf"].ValidationMessages!);
        Assert.Contains("CHECK_DIGIT_VALID", fields["cpf"].ValidationMessages!);
    }
}
