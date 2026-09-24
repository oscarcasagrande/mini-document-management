using DocReader.Domain.Validation;
using Xunit;

namespace DocReader.UnitTests.Domain;

public sealed class BrazilianDateTests
{
    private static readonly DateOnly Today = new(2026, 9, 24);

    [Theory]
    [InlineData("14/03/1985", 1985, 3, 14)]
    [InlineData("14-03-1985", 1985, 3, 14)]
    [InlineData("14.03.1985", 1985, 3, 14)]
    [InlineData("14 03 1985", 1985, 3, 14)]
    [InlineData("1/1/2000", 2000, 1, 1)]
    [InlineData("29/02/2024", 2024, 2, 29)]
    public void Aceita_os_separadores_que_o_ocr_costuma_produzir(string value, int year, int month, int day)
    {
        Assert.True(BrazilianDate.TryParse(value, out var parsed));
        Assert.Equal(new DateOnly(year, month, day), parsed);
    }

    [Theory]
    [InlineData("31/02/1985")]
    [InlineData("29/02/2023")]
    [InlineData("00/03/1985")]
    [InlineData("14/13/1985")]
    [InlineData("32/01/1985")]
    public void Recusa_data_que_nao_existe_no_calendario(string value)
    {
        Assert.False(BrazilianDate.TryParse(value, out _));
    }

    [Theory]
    [InlineData("1985-03-14")]
    [InlineData("14/03/85")]
    [InlineData("14/03")]
    [InlineData("14/03/1985/99")]
    [InlineData("nascimento")]
    [InlineData("")]
    [InlineData(null)]
    public void Recusa_formato_fora_de_dia_mes_ano_com_quatro_digitos(string? value)
    {
        Assert.False(BrazilianDate.TryParse(value, out _));
    }

    [Fact]
    public void Nao_devolve_valor_parcial_quando_falha()
    {
        Assert.False(BrazilianDate.TryParse("31/02/1985", out var parsed));
        Assert.Equal(default, parsed);
    }

    [Theory]
    [InlineData(1985, 3, 14, true)]
    [InlineData(1900, 1, 1, true)]
    [InlineData(2026, 9, 24, true)]
    [InlineData(2026, 9, 25, false)]
    [InlineData(2030, 1, 1, false)]
    [InlineData(1899, 12, 31, false)]
    public void Avalia_plausibilidade_de_data_de_nascimento(int year, int month, int day, bool expected)
    {
        Assert.Equal(expected, BrazilianDate.IsPlausibleBirthDate(new DateOnly(year, month, day), Today));
    }

    [Fact]
    public void Formata_em_iso_8601()
    {
        Assert.Equal("1985-03-14", BrazilianDate.ToIso(new DateOnly(1985, 3, 14)));
        Assert.Equal("2000-01-01", BrazilianDate.ToIso(new DateOnly(2000, 1, 1)));
    }
}
