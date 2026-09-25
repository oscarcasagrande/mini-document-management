using DocReader.Domain.Validation;
using Xunit;

namespace DocReader.UnitTests.Domain;

/// <summary>
/// Vetores calculados por uma transcrição independente do IsCNH do brdoc, cobrindo os três caminhos do
/// segundo dígito: sem desconto, com desconto que zera abaixo de 0 e com desconto normal.
/// </summary>
public sealed class CnhRegistrationTests
{
    [Theory]
    [InlineData("02650306461")]
    [InlineData("12345678900")]
    [InlineData("04512345621")]
    // sem desconto
    [InlineData("00000000778")]
    [InlineData("00000001460")]
    // desconto no 2º dígito, resto - 2 negativo (soma 11)
    [InlineData("00000018200")]
    [InlineData("00000033609")]
    [InlineData("00000084009")]
    // desconto no 2º dígito, resto - 2 não negativo
    [InlineData("00000004204")]
    [InlineData("00000007707")]
    [InlineData("00000030106")]
    // desconto e resto 10 no 2º dígito
    [InlineData("00000105708")]
    [InlineData("00000156108")]
    [InlineData("00000199508")]
    public void Registro_com_digitos_verificadores_corretos_e_valido(string value)
    {
        Assert.True(CnhRegistration.IsValid(value));
    }

    [Theory]
    [InlineData("02650306460")]
    [InlineData("02650306471")]
    [InlineData("12345678901")]
    [InlineData("04512345678")]
    [InlineData("00000018201")]
    [InlineData("00000105709")]
    public void Digito_verificador_errado_e_invalido(string value)
    {
        Assert.True(CnhRegistration.TryNormalize(value, out _));
        Assert.False(CnhRegistration.IsValid(value));
    }

    [Theory]
    [InlineData("00000000000")]
    [InlineData("11111111111")]
    [InlineData("99999999999")]
    public void Sequencia_de_digito_repetido_e_rejeitada_mesmo_quando_a_conta_fecha(string value)
    {
        Assert.False(CnhRegistration.IsValid(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0265030646")]
    [InlineData("026503064611")]
    [InlineData("0265030646A")]
    public void Formato_fora_do_padrao_nao_normaliza(string value)
    {
        Assert.False(CnhRegistration.TryNormalize(value, out _));
        Assert.False(CnhRegistration.IsValid(value));
    }

    [Fact]
    public void Separadores_de_mascara_sao_aceitos()
    {
        Assert.True(CnhRegistration.TryNormalize("026.503.064-61", out var normalized));
        Assert.Equal("02650306461", normalized);
    }

    [Fact]
    public void Digitos_verificadores_da_base_sao_devolvidos_como_texto()
    {
        Assert.Equal("61", CnhRegistration.CheckDigits("026503064"));
        Assert.Equal("00", CnhRegistration.CheckDigits("123456789"));
        Assert.Throws<ArgumentException>(() => CnhRegistration.CheckDigits("123"));
    }
}
