using DocReader.Domain.Validation;
using Xunit;

namespace DocReader.UnitTests.Domain;

public sealed class Stage3ValidatorsTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);

    // ------------------------------------------------------------ CEP

    [Theory]
    [InlineData("01000-000", "01000000")]
    [InlineData("01.000-000", "01000000")]
    [InlineData("01000000", "01000000")]
    [InlineData("04000 000", "04000000")]
    public void Cep_e_normalizado_para_oito_digitos(string value, string normalized)
    {
        Assert.True(PostalCode.TryNormalize(value, out var result));
        Assert.Equal(normalized, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0100-000")]
    [InlineData("010000000")]
    [InlineData("00000-000")]
    [InlineData("0100A-000")]
    public void Cep_fora_do_formato_nao_normaliza(string value)
    {
        Assert.False(PostalCode.TryNormalize(value, out _));
    }

    [Fact]
    public void Cep_recebe_mascara()
    {
        Assert.Equal("01000-000", PostalCode.Format("01000000"));
    }

    // ------------------------------------------------------------ UF

    [Theory]
    [InlineData("SP", "SP")]
    [InlineData("sp", "SP")]
    [InlineData("São Paulo", "SP")]
    [InlineData("SAO PAULO", "SP")]
    [InlineData("Distrito Federal", "DF")]
    [InlineData("Espírito Santo", "ES")]
    public void Uf_aceita_sigla_e_nome_por_extenso(string value, string code)
    {
        Assert.True(BrazilianState.TryNormalize(value, out var result));
        Assert.Equal(code, result);
    }

    [Theory]
    [InlineData("XX")]
    [InlineData("S")]
    [InlineData("Atlantida")]
    [InlineData("")]
    public void Uf_inexistente_e_rejeitada(string value)
    {
        Assert.False(BrazilianState.TryNormalize(value, out _));
        Assert.False(BrazilianState.IsValid(value));
    }

    [Fact]
    public void As_27_unidades_federativas_sao_reconhecidas()
    {
        string[] all =
        [
            "AC", "AL", "AP", "AM", "BA", "CE", "DF", "ES", "GO", "MA", "MT", "MS", "MG", "PA",
            "PB", "PR", "PE", "PI", "RJ", "RN", "RS", "RO", "RR", "SC", "SP", "SE", "TO"
        ];

        Assert.Equal(27, all.Length);
        Assert.All(all, code => Assert.True(BrazilianState.IsValid(code)));
    }

    // ------------------------------------------------------------ Dinheiro

    [Theory]
    [InlineData("R$ 100.000,00", "100000.00")]
    [InlineData("R$4.780,00", "4780.00")]
    [InlineData("4.780,00", "4780.00")]
    [InlineData("1,00", "1.00")]
    [InlineData("R$ 5.000,00", "5000.00")]
    [InlineData("4780,50", "4780.50")]
    [InlineData("1.234.567,89", "1234567.89")]
    public void Valor_em_reais_e_convertido_para_ponto_decimal(string value, string invariant)
    {
        Assert.True(BrazilianMoney.TryParse(value, out var amount));
        Assert.Equal(invariant, BrazilianMoney.ToInvariant(amount));
    }

    [Theory]
    [InlineData("")]
    [InlineData("R$")]
    [InlineData("100,0")]
    [InlineData("100,000")]
    [InlineData("1.23,45")]
    [InlineData("1.2345,67")]
    [InlineData("abc")]
    [InlineData("1,234,56")]
    public void Valor_fora_do_formato_e_rejeitado(string value)
    {
        Assert.False(BrazilianMoney.TryParse(value, out _));
    }

    // ------------------------------------------------------------ Datas por extenso e competência

    [Theory]
    [InlineData("24 de setembro de 2026", "2026-09-24")]
    [InlineData("24 DE SETEMBRO DE 2026", "2026-09-24")]
    [InlineData("1 de março de 2020", "2020-03-01")]
    [InlineData("1 de MARCO de 2020", "2020-03-01")]
    [InlineData("29 de fevereiro de 2024", "2024-02-29")]
    public void Data_por_extenso_vira_iso(string value, string iso)
    {
        Assert.True(BrazilianDate.TryParseLongForm(value, out var parsed));
        Assert.Equal(iso, BrazilianDate.ToIso(parsed));
    }

    [Theory]
    [InlineData("29 de fevereiro de 2025")]
    [InlineData("31 de abril de 2026")]
    [InlineData("24 de setembro 2026")]
    [InlineData("24 de smarch de 2026")]
    [InlineData("")]
    public void Data_por_extenso_impossivel_ou_fora_do_formato_e_rejeitada(string value)
    {
        Assert.False(BrazilianDate.TryParseLongForm(value, out _));
    }

    [Theory]
    [InlineData("09/2026", "2026-09")]
    [InlineData("9/2026", "2026-09")]
    [InlineData("12-2025", "2025-12")]
    public void Competencia_vira_ano_mes(string value, string iso)
    {
        Assert.True(BrazilianDate.TryParseMonthYear(value, out var result));
        Assert.Equal(iso, result);
    }

    [Theory]
    [InlineData("13/2026")]
    [InlineData("00/2026")]
    [InlineData("09/26")]
    [InlineData("09/1800")]
    public void Competencia_invalida_e_rejeitada(string value)
    {
        Assert.False(BrazilianDate.TryParseMonthYear(value, out _));
    }

    // ------------------------------------------------------------ MRZ (ICAO 9303, TD1)

    [Fact]
    public void Digito_verificador_do_icao_confere_com_o_exemplo_do_documento_9303()
    {
        // Exemplo publicado no ICAO 9303: L898902C36UTO7408122F1204159ZE184226B<<<<<10.
        Assert.Equal(6, MachineReadableZone.CheckDigit("L898902C3"));
        Assert.Equal(2, MachineReadableZone.CheckDigit("740812"));
        Assert.Equal(9, MachineReadableZone.CheckDigit("120415"));
    }

    [Fact]
    public void Caractere_fora_do_alfabeto_da_mrz_devolve_menos_um()
    {
        Assert.Equal(-1, MachineReadableZone.CheckDigit("ABC 123"));
    }

    [Fact]
    public void Mrz_td1_valida_e_lida_por_campo()
    {
        var lines = ValidTd1();

        Assert.True(MachineReadableZone.TryParseTd1(lines, Today, out var reading));
        Assert.True(reading.IsFullyValid);
        Assert.Equal("ID", reading.DocumentCode);
        Assert.Equal("BRA", reading.IssuingState);
        Assert.Equal("123456789", reading.DocumentNumber);
        Assert.Equal(new DateOnly(1985, 3, 14), reading.BirthDate);
        Assert.Equal(new DateOnly(2034, 5, 20), reading.ExpirationDate);
        Assert.Equal("BRA", reading.Nationality);
    }

    [Fact]
    public void Digito_verificador_errado_na_mrz_e_apontado_por_campo()
    {
        var lines = ValidTd1();
        // Estraga o dígito verificador da data de nascimento (posição 6 da linha 2).
        lines[1] = lines[1][..6] + "0" + lines[1][7..];

        Assert.True(MachineReadableZone.TryParseTd1(lines, Today, out var reading));
        Assert.False(reading.IsFullyValid);
        Assert.False(reading.BirthDateCheckValid);
        Assert.True(reading.DocumentNumberCheckValid);
        Assert.False(reading.CompositeCheckValid);
    }

    [Fact]
    public void Mrz_com_tamanho_errado_nao_e_interpretada()
    {
        Assert.False(MachineReadableZone.TryParseTd1(["IDBRA123", "X", "Y"], Today, out _));
        Assert.False(MachineReadableZone.TryParseTd1(ValidTd1()[..2], Today, out _));
    }

    [Fact]
    public void Nascimento_no_futuro_da_mrz_e_do_seculo_passado()
    {
        var lines = ValidTd1();
        // 850314 já cai em 1985; um yy maior que o ano corrente (26) também.
        Assert.True(MachineReadableZone.TryParseTd1(lines, Today, out var reading));
        Assert.Equal(1985, reading.BirthDate!.Value.Year);
    }

    /// <summary>MRZ montada pelo mesmo algoritmo do gerador de amostras (scripts/make-stage3-samples.py).</summary>
    private static string[] ValidTd1()
    {
        var number = "123456789";
        var line1 = "IDBRA" + number + MachineReadableZone.CheckDigit(number) + new string('<', 15);

        var birth = "850314";
        var expiry = "340520";
        var head = birth + MachineReadableZone.CheckDigit(birth) + "F" + expiry + MachineReadableZone.CheckDigit(expiry)
                   + "BRA" + new string('<', 11);
        var composite = line1[5..30] + head[0..7] + head[8..15] + head[18..29];
        var line2 = head + MachineReadableZone.CheckDigit(composite);

        var line3 = "SOUZA<<MARIA<APARECIDA<DA<SILVA".PadRight(30, '<')[..30];

        return [line1, line2, line3];
    }
}
