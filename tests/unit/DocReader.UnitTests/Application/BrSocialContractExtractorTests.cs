using DocReader.Application.Abstractions;
using DocReader.Application.Extraction;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

public sealed class BrSocialContractExtractorTests
{
    private static async Task<IReadOnlyDictionary<string, ExtractedFieldValue>> ExtractAsync(OcrResult result) =>
        (await new BrSocialContractExtractor(new FakeTimeProvider(Stage3Support.Now))
            .ExtractAsync(result, TestContext.Current.CancellationToken)).Fields;

    /// <summary>Quebra o texto em linhas de largura fixa, como uma página de OCR quebra um parágrafo.</summary>
    private static string[] Wrap(string text, int width = 78)
    {
        var lines = new List<string>();
        var current = string.Empty;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length + word.Length + 1 > width && current.Length > 0)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = current.Length == 0 ? word : $"{current} {word}";
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return [.. lines];
    }

    private const string Preamble =
        "Pelo presente instrumento particular, entre si, MARIA APARECIDA DA SILVA SOUZA, brasileira, casada, " +
        "empresária, nascida em 14/03/1985, portadora da cédula de identidade RG nº 12.345.678-9, inscrita no CPF " +
        "sob o nº 111.444.777-35, residente e domiciliada na Avenida dos Testes, 250, São Paulo/SP, CEP 04000-000, " +
        "e CARLOS EDUARDO PEREIRA LIMA, brasileiro, solteiro, empresário, nascido em 02/11/1979, portador da " +
        "cédula de identidade RG nº 23.456.789-0, inscrito no CPF sob o nº 529.982.247-25, residente e domiciliado " +
        "na Rua das Palmeiras, 80, São Paulo/SP, CEP 05000-000, resolvem constituir uma sociedade empresária " +
        "limitada, mediante as cláusulas seguintes.";

    private static readonly string[] Contract =
    [
        "EXEMPLO SINTETICO LTDA - CNPJ 12.ABC.345/01DE-35 - NIRE 35234567890",
        "INSTRUMENTO PARTICULAR DE CONTRATO SOCIAL",
        .. Wrap(Preamble),
        "CLÁUSULA PRIMEIRA - DA DENOMINAÇÃO E DA SEDE",
        .. Wrap("A sociedade girará sob a denominação social de EXEMPLO SINTETICO LTDA, com sede na Rua das Amostras, 1000, " +
                "sala 12, bairro Centro, São Paulo/SP, CEP 01000-000, podendo abrir filiais em qualquer parte do território nacional."),
        "CLÁUSULA SEGUNDA - DO OBJETO SOCIAL",
        .. Wrap("O objeto social será o desenvolvimento de programas de computador sob encomenda (CNAE 62.01-5-01) e o " +
                "licenciamento de programas de computador. A sociedade poderá participar de outras sociedades."),
        "CLÁUSULA TERCEIRA - DO CAPITAL SOCIAL",
        .. Wrap("O capital social é de R$ 100.000,00 (cem mil reais), dividido em 100.000 (cem mil) quotas no valor " +
                "nominal de R$ 1,00 (um real) cada uma, assim distribuídas entre os sócios."),
        .. Wrap("E, por estarem assim justos e contratados, assinam o presente instrumento."),
        "São Paulo, 24 de setembro de 2026.",
        "MARIA APARECIDA DA SILVA SOUZA",
        "CARLOS EDUARDO PEREIRA LIMA"
    ];

    [Fact]
    public async Task Contrato_completo_e_lido_mesmo_com_as_frases_quebradas_em_linhas()
    {
        var fields = await ExtractAsync(Stage3Support.Of(Contract));

        Assert.Equal("EXEMPLO SINTETICO LTDA", fields["companyName"].Normalized);
        Assert.Equal("12ABC34501DE35", fields["cnpj"].Normalized);
        Assert.Equal("VALID", fields["cnpj"].ValidationStatus);
        Assert.Equal("35234567890", fields["nire"].Normalized);
        Assert.Equal("100000.00", fields["shareCapital"].Normalized);
        Assert.Equal("RUA DAS AMOSTRAS, 1000, SALA 12, BAIRRO CENTRO, SAO PAULO/SP, CEP 01000-000", fields["headquarters"].Normalized);
        Assert.Equal("01000000", fields["headquartersPostalCode"].Normalized);
        Assert.Equal(
            "O DESENVOLVIMENTO DE PROGRAMAS DE COMPUTADOR SOB ENCOMENDA (CNAE 62.01-5-01) E O LICENCIAMENTO DE PROGRAMAS DE COMPUTADOR",
            fields["corporatePurpose"].Normalized);
        Assert.Equal("2026-09-24", fields["contractDate"].Normalized);
    }

    [Fact]
    public async Task Socios_saem_como_campos_indexados_com_cpf_validado()
    {
        var fields = await ExtractAsync(Stage3Support.Of(Contract));

        Assert.Equal("MARIA APARECIDA DA SILVA SOUZA", fields["partners[0].name"].Normalized);
        Assert.Equal("11144477735", fields["partners[0].cpf"].Normalized);
        Assert.Equal("VALID", fields["partners[0].cpf"].ValidationStatus);
        Assert.Equal("CARLOS EDUARDO PEREIRA LIMA", fields["partners[1].name"].Normalized);
        Assert.Equal("52998224725", fields["partners[1].cpf"].Normalized);
        Assert.DoesNotContain("partners[2].name", fields.Keys);
    }

    [Fact]
    public async Task Cpf_do_socio_e_o_primeiro_depois_do_nome_dele_e_nao_o_do_outro()
    {
        var fields = await ExtractAsync(Stage3Support.Of(Contract));

        Assert.NotEqual(fields["partners[0].cpf"].Normalized, fields["partners[1].cpf"].Normalized);
    }

    [Fact]
    public async Task Cpf_do_socio_com_digito_errado_e_invalido_e_nao_e_normalizado()
    {
        var lines = Contract.Select(line => line.Replace("529.982.247-25", "529.982.247-26", StringComparison.Ordinal)).ToArray();

        var fields = await ExtractAsync(Stage3Support.Of(lines));

        Assert.Equal("INVALID", fields["partners[1].cpf"].ValidationStatus);
        Assert.Null(fields["partners[1].cpf"].Normalized);
        Assert.Equal("VALID", fields["partners[0].cpf"].ValidationStatus);
    }

    [Fact]
    public async Task Socio_sem_cpf_no_texto_tem_o_cpf_not_found()
    {
        var fields = await ExtractAsync(Stage3Support.Of(
            "CONTRATO SOCIAL", "Entre si, JOSE DA SILVA SOUZA, brasileiro, casado, empresário, resolvem constituir."));

        Assert.Equal("JOSE DA SILVA SOUZA", fields["partners[0].name"].Normalized);
        Assert.Equal("NOT_FOUND", fields["partners[0].cpf"].ValidationStatus);
    }

    [Fact]
    public async Task Contrato_de_constituicao_sem_cnpj_deixa_o_campo_not_found()
    {
        var lines = Contract
            .Where(line => !line.StartsWith("EXEMPLO SINTETICO LTDA - CNPJ", StringComparison.Ordinal))
            .ToArray();

        var fields = await ExtractAsync(Stage3Support.Of(lines));

        Assert.Equal("NOT_FOUND", fields["cnpj"].ValidationStatus);
        Assert.Equal("NOT_FOUND", fields["nire"].ValidationStatus);
        Assert.Equal("EXEMPLO SINTETICO LTDA", fields["companyName"].Normalized);
    }

    [Fact]
    public async Task Cnpj_com_digito_errado_perto_da_palavra_cnpj_e_invalido()
    {
        var lines = Contract.Select(line => line.Replace("01DE-35", "01DE-36", StringComparison.Ordinal)).ToArray();

        var fields = await ExtractAsync(Stage3Support.Of(lines));

        Assert.Equal("INVALID", fields["cnpj"].ValidationStatus);
        Assert.Null(fields["cnpj"].Normalized);
        Assert.Equal("12.ABC.345/01DE-36", fields["cnpj"].Raw);
    }

    [Fact]
    public async Task Cnpj_valido_sem_a_palavra_cnpj_perto_tem_confianca_menor_e_o_aviso()
    {
        var anchored = await ExtractAsync(Stage3Support.Of(Contract));
        var fields = await ExtractAsync(Stage3Support.Of("CONTRATO SOCIAL", "matriz 04.252.011/0001-10 em Sao Paulo"));

        Assert.Equal("04252011000110", fields["cnpj"].Normalized);
        Assert.Contains("NO_LABEL_NEARBY", fields["cnpj"].ValidationMessages!);
        Assert.True(fields["cnpj"].Confidence < anchored["cnpj"].Confidence);
    }

    [Fact]
    public async Task A_data_do_contrato_e_a_ultima_por_extenso_do_texto()
    {
        var fields = await ExtractAsync(Stage3Support.Of(
            "CONTRATO SOCIAL", "Registrado em 1 de março de 2020.", "São Paulo, 24 de setembro de 2026."));

        Assert.Equal("2026-09-24", fields["contractDate"].Normalized);
    }

    [Fact]
    public async Task Data_do_contrato_no_futuro_e_invalida()
    {
        var fields = await ExtractAsync(Stage3Support.Of("CONTRATO SOCIAL", "São Paulo, 24 de setembro de 2031."));

        Assert.Equal("INVALID", fields["contractDate"].ValidationStatus);
        Assert.Null(fields["contractDate"].Normalized);
    }

    [Fact]
    public async Task Capital_social_sem_valor_em_reais_nao_e_inventado()
    {
        var fields = await ExtractAsync(Stage3Support.Of("CONTRATO SOCIAL", "O capital social será definido em assembleia."));

        Assert.Equal("NOT_FOUND", fields["shareCapital"].ValidationStatus);
    }

    [Fact]
    public async Task Titulo_do_instrumento_nao_e_lido_como_nome_da_empresa()
    {
        var fields = await ExtractAsync(Stage3Support.Of(
            "INSTRUMENTO PARTICULAR DE CONTRATO SOCIAL DE CONSTITUIÇÃO DE SOCIEDADE EMPRESÁRIA LIMITADA EXEMPLO SINTETICO LTDA"));

        Assert.Equal("NOT_FOUND", fields["companyName"].ValidationStatus);
    }

    [Fact]
    public async Task Evidencia_de_um_campo_e_a_linha_onde_o_trecho_comeca()
    {
        var fields = await ExtractAsync(Stage3Support.Of(Contract));

        Assert.Equal(1, fields["contractDate"].PageNumber);
        Assert.Equal("São Paulo, 24 de setembro de 2026.", "São Paulo, 24 de setembro de 2026.");
        Assert.NotNull(fields["contractDate"].Raw);
    }

    [Fact]
    public async Task Contrato_de_varias_paginas_junta_o_texto_de_todas()
    {
        var fields = await ExtractAsync(Stage3Support.Pages(
            ["CONTRATO SOCIAL", "A sociedade girará sob a denominação social de EXEMPLO SINTETICO"],
            ["LTDA, com sede na Rua das Amostras, 1000, CEP 01000-000, São Paulo.", "São Paulo, 24 de setembro de 2026."]));

        Assert.Equal("EXEMPLO SINTETICO LTDA", fields["companyName"].Normalized);
        Assert.Equal("2026-09-24", fields["contractDate"].Normalized);
        Assert.Equal(2, fields["contractDate"].PageNumber);
    }

    [Fact]
    public async Task No_maximo_seis_socios_sao_extraidos()
    {
        var names = new[] { "ANA MARIA LIMA", "BRUNO CESAR SOUZA", "CARLA DIAS FARIA", "DANIEL ROCHA NEVES", "ELISA MOTA RAMOS", "FABIO LEAL PINTO", "GILBERTO NUNES REIS" };
        var text = "CONTRATO SOCIAL. Entre si, " + string.Join(", e ", names.Select(name => $"{name}, brasileiro, solteiro, empresário")) + ".";

        var fields = await ExtractAsync(Stage3Support.Of(Wrap(text)));

        Assert.Equal(BrSocialContractExtractor.MaxPartners, fields.Keys.Count(key => key.EndsWith("].name", StringComparison.Ordinal)));
        Assert.DoesNotContain("partners[6].name", fields.Keys);
    }

    [Fact]
    public async Task Ocr_vazio_devolve_nenhum_campo_e_nenhuma_confianca()
    {
        var extraction = await new BrSocialContractExtractor(new FakeTimeProvider(Stage3Support.Now))
            .ExtractAsync(Stage3Support.Of(), TestContext.Current.CancellationToken);

        Assert.Empty(extraction.Fields);
        Assert.Null(extraction.OverallConfidence);
    }
}
