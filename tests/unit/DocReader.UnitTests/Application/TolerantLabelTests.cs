using DocReader.Application.Extraction;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static DocReader.UnitTests.Application.Stage3Support;

namespace DocReader.UnitTests.Application;

/// <summary>
/// Os mecanismos que os documentos reais exigiram, isolados em layouts pequenos: rótulo bilíngue, colado e com
/// numerador; nome em várias linhas sem atravessar o rótulo seguinte; bloco vertical que não é valor; data com
/// espaço só perto do rótulo.
/// </summary>
public sealed class TolerantLabelTests
{
    private static readonly FakeTimeProvider Clock = new(Now);

    private static async Task<IReadOnlyDictionary<string, DocReader.Application.Abstractions.ExtractedFieldValue>> Cin(params Placed[] items) =>
        (await new BrCinExtractor(Clock).ExtractAsync(Laid(items), CancellationToken.None)).Fields;

    private static async Task<IReadOnlyDictionary<string, DocReader.Application.Abstractions.ExtractedFieldValue>> Cnh(params Placed[] items) =>
        (await new BrCnhExtractor(Clock).ExtractAsync(Laid(items), CancellationToken.None)).Fields;

    [Fact]
    public async Task Nome_em_duas_linhas_nao_atravessa_o_rotulo_do_campo_seguinte()
    {
        var fields = await Cin(
            new Placed("NOME/NAME", 100, 100, 200),
            new Placed("MARINA", 100, 130, 100),
            new Placed("DE SOUZA LIMA", 100, 160, 200),
            new Placed("SEXO/SEX", 100, 190, 100),
            new Placed("FEM", 100, 220, 60));

        Assert.Equal("MARINA DE SOUZA LIMA", fields["name"].Normalized);
    }

    [Fact]
    public async Task Nome_de_uma_linha_so_com_uma_palavra_e_sem_continuacao_continua_nao_encontrado()
    {
        var fields = await Cin(new Placed("NOME", 100, 100, 100), new Placed("MARINA", 100, 130, 100));

        Assert.Equal("NOT_FOUND", fields["name"].ValidationStatus);
    }

    [Fact]
    public async Task Bloco_vertical_alinhado_ao_rotulo_nao_e_valor()
    {
        // A numeração impressa na vertical, na borda da carteira, chega como um bloco alto e estreito no meio da linha.
        var fields = await Cin(
            new Placed("NATURALIDADE", 100, 300, 150),
            new Placed("B17890", 600, 212, 20, 200),
            new Placed("UTOPIA/UT", 100, 330, 150));

        Assert.Equal("UTOPIA/UT", fields["birthPlace"].Normalized);
    }

    [Fact]
    public async Task Rotulo_colado_com_numerador_e_sinal_de_numero_casa()
    {
        var fields = await Cnh(
            new Placed("5N°REGISTRO", 500, 100, 120),
            new Placed("04512345621", 500, 130, 120));

        Assert.Equal("04512345621", fields["registrationNumber"].Normalized);
        Assert.Equal("VALID", fields["registrationNumber"].ValidationStatus);
    }

    [Fact]
    public async Task Rotulo_com_numerador_duplo_casa()
    {
        var fields = await Cnh(
            new Placed("2e 1 NOME E SOBRENOME", 100, 100, 220),
            new Placed("MARIA DE SOUZA LIMA", 100, 130, 220));

        Assert.Equal("MARIA DE SOUZA LIMA", fields["name"].Normalized);
    }

    [Fact]
    public async Task Rotulo_com_uma_letra_errada_pelo_ocr_casa()
    {
        var fields = await Cin(
            new Placed("FILUACAO", 100, 100, 100),
            new Placed("LUIS SOUZA LIMA", 100, 130, 200),
            new Placed("ANA MARIA SOUZA LIMA", 100, 160, 200));

        Assert.Equal("LUIS SOUZA LIMA", fields["fatherName"].Normalized);
        Assert.Equal("ANA MARIA SOUZA LIMA", fields["motherName"].Normalized);
    }

    [Fact]
    public async Task Rotulo_bilingue_de_data_casa_pelo_lado_em_portugues()
    {
        var fields = await Cin(
            new Placed("DATA DE NASC / DATE OF BIRTH", 100, 100, 260),
            new Placed("12 07 1975", 100, 130, 120));

        Assert.Equal("1975-07-12", fields["birthDate"].Normalized);
        Assert.Equal("12 07 1975", fields["birthDate"].Raw);
    }

    [Fact]
    public async Task Tres_numeros_separados_por_espaco_sem_rotulo_nao_viram_data()
    {
        var fields = await Cin(new Placed("12 07 1975", 100, 130, 120));

        Assert.Equal("NOT_FOUND", fields["birthDate"].ValidationStatus);
    }

    [Fact]
    public async Task Data_impossivel_com_espaco_e_reportada_como_invalida_e_nao_inventada()
    {
        var fields = await Cin(
            new Placed("DATA DE NASCIMENTO", 100, 100, 200),
            new Placed("31 02 1975", 100, 130, 120));

        Assert.Equal("INVALID", fields["birthDate"].ValidationStatus);
        Assert.Null(fields["birthDate"].Normalized);
    }

    [Fact]
    public async Task Mrz_com_a_primeira_linha_partida_em_dois_blocos_na_mesma_linha_visual()
    {
        var fields = await Cin(
            new Placed("IDBRA123456789<<<<<<<<<<", 50, 900, 380),
            new Placed("<<<<<<<", 440, 902, 120),
            new Placed("7501122F2510214BRA<<<<<<<<<<<2", 50, 950, 500),
            new Placed("SILVA<<MARIA<<<<<<<<<<<<<<<<<<<", 50, 1000, 500));

        Assert.NotEqual("NOT_FOUND", fields["mrz"].ValidationStatus);
    }
}
