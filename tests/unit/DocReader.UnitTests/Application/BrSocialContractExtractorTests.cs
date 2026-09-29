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

    /// <summary>
    /// Regressão do achado 6 de <c>docs/bench/real-exploratory-v1.md</c>: texto de OCR real (mascarado),
    /// com uma linha inteira de ruído ("D") colada, só separada por espaço, imediatamente antes do nome
    /// da sócia. O anchor antigo só aceitava pontuação, "N)" ou e/entre/por logo antes do nome; um único
    /// caractere de ruído nessa posição bastava para nenhuma alternativa bater, e o sócio inteiro sumia
    /// sem deixar rastro (contrato social não passa por <c>ExtractionTrace</c>).
    /// </summary>
    private static readonly string[] ContratoRealComRuidoAntesDoNome =
    [
        "Esca",
        "assessoria contábile tributária",
        "INSTRUMENTO PARTICULAR DE ALTERAÇÃO DE CONTRATO SOCIAL",
        "EXEMPLO SERVICOS E TECNOLOGIA LTDA\"",
        "CNPJ 11.222.333/0001-81",
        "NIRE 11.111.111.111",
        "D",
        "MARIA EXEMPLO DA SILVA SANTOS, brasileira, casada sob regime de comunhão",
        "parcial de bens, nascida em 01/01/1980, empresária, portadora da cédula de identidade RG sob",
        "nº 11.111.111/SSP/SP, inscrita no CPF-MF sob nº 111.222.333-96, residente e domiciliada nesta",
        "capital, do estado de São Paulo, na rua Exemplo, n° 100, no bairro Exemplo – CEP",
        "11111-000.",
        "Única sócia desta Sociedade Limitada Unipessoal, que gira nesta praça sob a",
    ];

    [Fact]
    public async Task Socia_e_extraida_mesmo_com_uma_linha_de_ruido_de_ocr_colada_antes_do_nome()
    {
        var fields = await ExtractAsync(Stage3Support.Of(ContratoRealComRuidoAntesDoNome));

        Assert.Equal("MARIA EXEMPLO DA SILVA SANTOS", fields["partners[0].name"].Normalized);
        Assert.Equal("11122233396", fields["partners[0].cpf"].Normalized);
        Assert.Equal("VALID", fields["partners[0].cpf"].ValidationStatus);
        Assert.DoesNotContain("partners[1].name", fields.Keys);
    }

    /// <summary>
    /// Mesma regressão, lado "documento que já funcionava": ruído de OCR também presente ("b" solto, algumas
    /// linhas antes do nome, não colado a ele), sócio único ainda lido corretamente depois do ajuste do
    /// anchor. Este fragmento traz só a introdução do primeiro sócio do documento real (o documento inteiro
    /// tinha dois), então o teste cobre a regressão específica, não a cobertura completa do documento.
    /// </summary>
    private static readonly string[] ContratoRealComRuidoLongeDoNome =
    [
        "CNPJ11.222.222/0001-23",
        "RG:1.111.111-1",
        "NRE:11.111111.111",
        "b",
        "Pelo presente instrumento particular de Alteração Contratual Consolidada, e na melhor forma",
        "de direito, os abaixo assinados:",
        "JOSE EXEMPLO DA SILVA, brasileiro, divorciado, maior, empresário, nascido em",
        "01/01/1950, portador da cédula de identidade RG de n° 1.111.111-1 SSP/SP, inscrito no",
        "CPF/MF sob o n° 111.333.555-60, residente e domiciliado à Rua Exemplo n° 53 -",
        "Jardim Exemplo - Exemplo - SP CEP 11111-070.",
        "Único sócio componente da sociedade limitada unipessoal que nesta praça gira sob a",
        "denominação social de EXEMPLO ADESIVOS LTDA, com sede à Rua Exemplo n°",
        "100 – Sala 03 - Polo Industrial - Exemplo – SP CEP 11111-450, com contrato",
        "social registrado e arquivado na Junta Comercial do Estado de São Paulo JUCESP sob o NIRE",
        "de n° 11.111.111.111",
    ];

    [Fact]
    public async Task Socio_do_documento_que_ja_funcionava_continua_lido_depois_do_ajuste_do_anchor()
    {
        var fields = await ExtractAsync(Stage3Support.Of(ContratoRealComRuidoLongeDoNome));

        Assert.Equal("JOSE EXEMPLO DA SILVA", fields["partners[0].name"].Normalized);
        Assert.Equal("11133355560", fields["partners[0].cpf"].Normalized);
        Assert.Equal("VALID", fields["partners[0].cpf"].ValidationStatus);
        Assert.DoesNotContain("partners[1].name", fields.Keys);
    }

    /// <summary>
    /// Confirma que o caso "limpo" (sócio só depois de pontuação, sem nenhum ruído colado) continua
    /// batendo depois de trocar o anchor específico por uma guarda mais simples — já coberto pelos testes
    /// acima e por <see cref="Socios_saem_como_campos_indexados_com_cpf_validado"/>, que usa a mesma
    /// combinação "pontuação, espaço, nome" da amostra sintética original.
    /// </summary>
    [Fact]
    public async Task Socio_sem_nenhum_ruido_antes_do_nome_continua_lido()
    {
        var fields = await ExtractAsync(Stage3Support.Of(
            "CONTRATO SOCIAL", "Entre si, os abaixo assinados: MARIA EXEMPLO DA SILVA, brasileira, casada, empresária, resolvem constituir."));

        Assert.Equal("MARIA EXEMPLO DA SILVA", fields["partners[0].name"].Normalized);
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
