using DocReader.Domain.Validation;
using Xunit;

namespace DocReader.UnitTests.Domain;

/// <summary>Casos de teste obrigatórios do RF-012 (CNPJ alfanumérico) e o que decorre deles.</summary>
public sealed class CnpjTests
{
    [Theory]
    [InlineData("12.ABC.345/01DE-35", "12ABC34501DE35")]
    [InlineData("12.abc.345/01de-35", "12ABC34501DE35")]
    [InlineData("04.252.011/0001-10", "04252011000110")]
    public void Cnpj_valido_e_normalizado_para_14_posicoes_em_caixa_alta(string value, string normalized)
    {
        Assert.True(Cnpj.TryNormalize(value, out var result));
        Assert.Equal(normalized, result);
        Assert.True(Cnpj.IsValid(value));
    }

    [Fact]
    public void Digito_verificador_incorreto_e_invalido()
    {
        Assert.True(Cnpj.TryNormalize("12.ABC.345/01DE-36", out var normalized));
        Assert.Equal("12ABC34501DE36", normalized);
        Assert.False(Cnpj.IsValid("12.ABC.345/01DE-36"));
    }

    [Fact]
    public void Digito_verificador_nao_numerico_nem_chega_a_normalizar()
    {
        Assert.False(Cnpj.TryNormalize("12.ABC.345/01DE-3X", out var normalized));
        Assert.Equal(string.Empty, normalized);
        Assert.False(Cnpj.IsValid("12.ABC.345/01DE-3X"));
    }

    [Fact]
    public void Doze_primeiras_posicoes_iguais_sao_rejeitadas_mesmo_quando_o_modulo_fecha()
    {
        Assert.True(Cnpj.TryNormalize("AA.AAA.AAA/AAAA-45", out var normalized));
        Assert.Equal("AAAAAAAAAAAA45", normalized);
        Assert.False(Cnpj.IsValid("AA.AAA.AAA/AAAA-45"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12.ABC.345/01DE")]
    [InlineData("12.ABC.345/01DE-355")]
    [InlineData("12.AB!.345/01DE-35")]
    public void Formato_fora_do_padrao_nao_normaliza(string value)
    {
        Assert.False(Cnpj.TryNormalize(value, out _));
        Assert.False(Cnpj.IsValid(value));
    }

    [Fact]
    public void Acentuacao_e_removida_antes_de_validar()
    {
        Assert.True(Cnpj.TryNormalize("12.ÁBC.345/01DE-35", out var normalized));
        Assert.Equal("12ABC34501DE35", normalized);
    }

    [Fact]
    public void Mascara_e_aplicada_ao_valor_normalizado()
    {
        Assert.Equal("12.ABC.345/01DE-35", Cnpj.Format("12ABC34501DE35"));
        Assert.Throws<ArgumentException>(() => Cnpj.Format("123"));
    }
}
