using DocReader.Domain.Validation;
using Xunit;

namespace DocReader.UnitTests.Domain;

public sealed class CpfTests
{
    [Theory]
    [InlineData("111.444.777-35")]
    [InlineData("11144477735")]
    [InlineData("529.982.247-25")]
    [InlineData("52998224725")]
    [InlineData(" 111.444.777-35 ")]
    public void Aceita_cpf_com_digito_verificador_correto(string value)
    {
        Assert.True(Cpf.IsValid(value));
    }

    [Theory]
    [InlineData("111.444.777-36")]
    [InlineData("111.444.777-45")]
    [InlineData("529.982.247-26")]
    public void Recusa_cpf_com_digito_verificador_errado(string value)
    {
        Assert.False(Cpf.IsValid(value));
    }

    [Theory]
    [InlineData("000.000.000-00")]
    [InlineData("111.111.111-11")]
    [InlineData("222.222.222-22")]
    [InlineData("99999999999")]
    public void Recusa_sequencia_de_digito_repetido_mesmo_quando_o_modulo_11_fecha(string value)
    {
        Assert.False(Cpf.IsValid(value));
    }

    [Theory]
    [InlineData("111.444.777-3")]
    [InlineData("111.444.777-355")]
    [InlineData("111444777")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Recusa_valor_sem_onze_digitos(string? value)
    {
        Assert.False(Cpf.IsValid(value));
    }

    [Theory]
    [InlineData("111.444.777-3A")]
    [InlineData("CPF 111.444.777-35")]
    [InlineData("111,444,777-35")]
    [InlineData("111.444.777_35")]
    public void Recusa_caractere_que_nao_e_digito_nem_separador_de_mascara(string value)
    {
        Assert.False(Cpf.IsValid(value));
    }

    [Theory]
    [InlineData("111.444.777-35", "11144477735")]
    [InlineData("111 444 777 35", "11144477735")]
    [InlineData("111.444.777/35", "11144477735")]
    public void Normaliza_removendo_a_mascara(string value, string expected)
    {
        Assert.True(Cpf.TryNormalize(value, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Normalizacao_falha_sem_devolver_valor_parcial()
    {
        Assert.False(Cpf.TryNormalize("111.444", out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void Aplica_a_mascara_no_valor_normalizado()
    {
        Assert.Equal("111.444.777-35", Cpf.Format("11144477735"));
    }

    [Fact]
    public void Recusa_formatar_valor_com_tamanho_errado()
    {
        Assert.Throws<ArgumentException>(() => Cpf.Format("1114447773"));
    }

    [Fact]
    public void Todo_cpf_valido_sobrevive_a_ida_e_volta_da_mascara()
    {
        // Varre um espaço de bases e confere que normalizar, formatar e validar são consistentes.
        var checkedCount = 0;

        for (var basis = 0; basis < 1_000_000; basis += 7_919)
        {
            var nineDigits = basis.ToString("D9", System.Globalization.CultureInfo.InvariantCulture);
            var candidate = nineDigits + "00";

            if (!Cpf.TryNormalize(candidate, out var normalized))
            {
                continue;
            }

            var formatted = Cpf.Format(normalized);
            Assert.True(Cpf.TryNormalize(formatted, out var roundTripped));
            Assert.Equal(normalized, roundTripped);
            Assert.Equal(Cpf.IsValid(candidate), Cpf.IsValid(formatted));
            checkedCount++;
        }

        Assert.True(checkedCount > 100);
    }
}
