using DocReader.Application.Abstractions;
using DocReader.Application.Extraction;
using DocReader.Domain.Validation;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DocReader.UnitTests.Application;

public sealed class BrCinExtractorTests
{
    private static async Task<IReadOnlyDictionary<string, ExtractedFieldValue>> ExtractAsync(OcrResult result) =>
        (await new BrCinExtractor(new FakeTimeProvider(Stage3Support.Now))
            .ExtractAsync(result, TestContext.Current.CancellationToken)).Fields;

    private static Task<IReadOnlyDictionary<string, ExtractedFieldValue>> ExtractAsync(params string[] lines) =>
        ExtractAsync(Stage3Support.Of(lines));

    /// <summary>Mesmo algoritmo do gerador de amostras: MRZ TD1 do documento 123456789.</summary>
    private static string[] Mrz(string birth = "850314")
    {
        var number = "123456789";
        var line1 = "IDBRA" + number + MachineReadableZone.CheckDigit(number) + new string('<', 15);
        var expiry = "340520";
        var head = birth + MachineReadableZone.CheckDigit(birth) + "F" + expiry + MachineReadableZone.CheckDigit(expiry)
                   + "BRA" + new string('<', 11);
        var composite = line1[5..30] + head[0..7] + head[8..15] + head[18..29];

        return [line1, head + MachineReadableZone.CheckDigit(composite), "SOUZA<<MARIA<APARECIDA<DA<SILVA".PadRight(30, '<')[..30]];
    }

    [Fact]
    public async Task Rotulo_acima_do_valor_em_ordem_de_leitura_extrai_os_campos()
    {
        var fields = await ExtractAsync(
            "REPÚBLICA FEDERATIVA DO BRASIL", "CARTEIRA DE IDENTIDADE NACIONAL",
            "NOME", "MARIA APARECIDA DA SILVA SOUZA",
            "DATA DE NASCIMENTO", "14/03/1985",
            "CPF", "111.444.777-35",
            "REGISTRO GERAL", "12.345.678-9",
            "NATURALIDADE", "São Paulo - SP",
            "DATA DE EXPEDIÇÃO", "20/05/2024",
            "DATA DE VALIDADE", "20/05/2034");

        Assert.Equal("MARIA APARECIDA DA SILVA SOUZA", fields["name"].Normalized);
        Assert.Equal("11144477735", fields["cpf"].Normalized);
        Assert.Equal("VALID", fields["cpf"].ValidationStatus);
        Assert.Equal("123456789", fields["rg"].Normalized);
        Assert.Equal("12.345.678-9", fields["rg"].Raw);
        Assert.Equal("1985-03-14", fields["birthDate"].Normalized);
        Assert.Equal("2024-05-20", fields["issueDate"].Normalized);
        Assert.Equal("2034-05-20", fields["expirationDate"].Normalized);
        Assert.Equal("SAO PAULO - SP", fields["birthPlace"].Normalized);
    }

    [Fact]
    public async Task Rotulo_e_valor_na_mesma_linha_com_dois_pontos()
    {
        var fields = await ExtractAsync(
            "CARTEIRA DE IDENTIDADE", "Nome: MARIA APARECIDA DA SILVA SOUZA", "CPF: 111.444.777-35", "Data de nascimento: 14/03/1985");

        Assert.Equal("MARIA APARECIDA DA SILVA SOUZA", fields["name"].Normalized);
        Assert.Equal("11144477735", fields["cpf"].Normalized);
        Assert.Equal("1985-03-14", fields["birthDate"].Normalized);
    }

    [Fact]
    public async Task Nome_social_nao_e_lido_como_nome()
    {
        var fields = await ExtractAsync("NOME SOCIAL", "MARIANA SOUZA", "NOME", "MARIA APARECIDA DA SILVA SOUZA");

        Assert.Equal("MARIA APARECIDA DA SILVA SOUZA", fields["name"].Normalized);
    }

    [Fact]
    public async Task Cpf_com_digito_errado_e_invalido_e_preserva_o_que_foi_lido()
    {
        var fields = await ExtractAsync("CARTEIRA DE IDENTIDADE", "CPF", "111.444.777-99");

        Assert.Equal("INVALID", fields["cpf"].ValidationStatus);
        Assert.Equal("111.444.777-99", fields["cpf"].Raw);
        Assert.Null(fields["cpf"].Normalized);
        Assert.Equal(["CHECK_DIGIT_INVALID"], fields["cpf"].ValidationMessages);
    }

    [Fact]
    public async Task Rg_com_sigla_de_estado_e_x_no_final_e_normalizado()
    {
        var fields = await ExtractAsync("REGISTRO GERAL", "MG-12.345.678-X");

        Assert.Equal("12345678X", fields["rg"].Normalized);
        Assert.Equal("VALID", fields["rg"].ValidationStatus);
    }

    [Fact]
    public async Task Data_de_nascimento_no_futuro_e_invalida()
    {
        var fields = await ExtractAsync("DATA DE NASCIMENTO", "14/03/2030");

        Assert.Equal("INVALID", fields["birthDate"].ValidationStatus);
        Assert.Null(fields["birthDate"].Normalized);
        Assert.Equal(["DATE_INVALID"], fields["birthDate"].ValidationMessages);
    }

    [Fact]
    public async Task Validade_no_futuro_e_aceita_mas_nascimento_nao()
    {
        var fields = await ExtractAsync("DATA DE VALIDADE", "20/05/2034");

        Assert.Equal("VALID", fields["expirationDate"].ValidationStatus);
        Assert.Equal("2034-05-20", fields["expirationDate"].Normalized);
    }

    [Fact]
    public async Task Filiacao_em_bloco_sai_uncertain_porque_a_ordem_e_suposta()
    {
        var fields = await ExtractAsync("FILIAÇÃO", "JOSE DA SILVA SOUZA", "ANA MARIA APARECIDA");

        Assert.Equal("JOSE DA SILVA SOUZA", fields["fatherName"].Normalized);
        Assert.Equal("UNCERTAIN", fields["fatherName"].ValidationStatus);
        Assert.Equal(["FILIATION_ORDER_ASSUMED"], fields["fatherName"].ValidationMessages);
        Assert.Equal("ANA MARIA APARECIDA", fields["motherName"].Normalized);
        Assert.Equal("UNCERTAIN", fields["motherName"].ValidationStatus);
    }

    [Fact]
    public async Task Pai_e_mae_com_rotulo_proprio_sao_valid()
    {
        var fields = await ExtractAsync("NOME DO PAI", "JOSE DA SILVA SOUZA", "NOME DA MÃE", "ANA MARIA APARECIDA");

        Assert.Equal("VALID", fields["fatherName"].ValidationStatus);
        Assert.Equal("JOSE DA SILVA SOUZA", fields["fatherName"].Normalized);
        Assert.Equal("VALID", fields["motherName"].ValidationStatus);
        Assert.Equal("ANA MARIA APARECIDA", fields["motherName"].Normalized);
    }

    [Fact]
    public async Task Filiacao_com_uma_so_linha_de_nome_preenche_so_o_primeiro()
    {
        var fields = await ExtractAsync("FILIAÇÃO", "JOSE DA SILVA SOUZA");

        Assert.Equal("UNCERTAIN", fields["fatherName"].ValidationStatus);
        Assert.Equal("NOT_FOUND", fields["motherName"].ValidationStatus);
    }

    // ------------------------------------------------------------ MRZ

    [Fact]
    public async Task Mrz_valida_confere_com_a_data_de_nascimento_impressa()
    {
        var lines = new List<string> { "DATA DE NASCIMENTO", "14/03/1985" };
        lines.AddRange(Mrz());

        var fields = await ExtractAsync([.. lines]);

        Assert.Equal("VALID", fields["mrz"].ValidationStatus);
        Assert.Equal(["MRZ_CHECK_VALID", "MRZ_BIRTH_DATE_MATCH"], fields["mrz"].ValidationMessages);
        Assert.Equal(string.Join('\n', Mrz()), fields["mrz"].Normalized);
    }

    [Fact]
    public async Task Mrz_com_data_diferente_da_impressa_sai_uncertain()
    {
        var lines = new List<string> { "DATA DE NASCIMENTO", "14/03/1990" };
        lines.AddRange(Mrz());

        var fields = await ExtractAsync([.. lines]);

        Assert.Equal("UNCERTAIN", fields["mrz"].ValidationStatus);
        Assert.Contains("MRZ_BIRTH_DATE_MISMATCH", fields["mrz"].ValidationMessages!);
    }

    [Fact]
    public async Task Mrz_com_digito_verificador_errado_e_invalid()
    {
        var mrz = Mrz();
        mrz[1] = mrz[1][..6] + "0" + mrz[1][7..];

        var fields = await ExtractAsync(mrz);

        Assert.Equal("INVALID", fields["mrz"].ValidationStatus);
        Assert.Equal(["MRZ_CHECK_INVALID"], fields["mrz"].ValidationMessages);
    }

    [Fact]
    public async Task Mrz_com_preenchimento_lido_como_guilhemete_e_espacos_ainda_e_lida()
    {
        var mrz = Mrz().Select(line => line.Replace("<", "«").Insert(10, " ")).ToArray();

        var fields = await ExtractAsync(mrz);

        Assert.Equal("VALID", fields["mrz"].ValidationStatus);
    }

    [Fact]
    public async Task Mrz_com_tamanho_de_linha_inesperado_sai_uncertain_sem_valor_normalizado()
    {
        var mrz = Mrz();
        mrz[2] = mrz[2][..28];

        var fields = await ExtractAsync(mrz);

        Assert.Equal("UNCERTAIN", fields["mrz"].ValidationStatus);
        Assert.Null(fields["mrz"].Normalized);
        Assert.Equal(["MRZ_LENGTH_UNEXPECTED"], fields["mrz"].ValidationMessages);
    }

    [Fact]
    public async Task Sem_mrz_o_campo_e_not_found()
    {
        var fields = await ExtractAsync("CARTEIRA DE IDENTIDADE", "NOME", "MARIA APARECIDA DA SILVA SOUZA");

        Assert.Equal("NOT_FOUND", fields["mrz"].ValidationStatus);
    }

    // ------------------------------------------------------------ Geometria

    [Fact]
    public async Task Em_duas_colunas_o_valor_e_o_que_esta_abaixo_do_rotulo_e_nao_o_proximo_na_leitura()
    {
        // O OCR devolve linha a linha: NOME e CPF lado a lado, depois os valores.
        var fields = await ExtractAsync(Stage3Support.Laid(
            new("NOME", 80, 200),
            new("CPF", 640, 200),
            new("MARIA APARECIDA DA SILVA SOUZA", 80, 236, 420, 34),
            new("111.444.777-35", 640, 236, 250, 34)));

        Assert.Equal("MARIA APARECIDA DA SILVA SOUZA", fields["name"].Normalized);
        Assert.Equal("11144477735", fields["cpf"].Normalized);
        Assert.Equal("VALID", fields["cpf"].ValidationStatus);
    }

    [Fact]
    public async Task Nome_partido_em_dois_blocos_e_juntado_mas_a_coluna_ao_lado_nao_entra()
    {
        var fields = await ExtractAsync(Stage3Support.Laid(
            new("NOME", 80, 200),
            new("CPF", 640, 200),
            new("MARIA APARECIDA", 80, 236, 220, 34),
            new("DA SILVA SOUZA", 310, 236, 230, 34),
            new("111.444.777-35", 640, 236, 250, 34)));

        Assert.Equal("MARIA APARECIDA DA SILVA SOUZA", fields["name"].Normalized);
        Assert.Equal("11144477735", fields["cpf"].Normalized);
    }

    [Fact]
    public async Task Valor_a_direita_do_rotulo_na_mesma_linha_visual_e_lido()
    {
        var fields = await ExtractAsync(Stage3Support.Laid(
            new("DATA DE NASCIMENTO", 80, 300, 300, 26),
            new("14/03/1985", 420, 298, 200, 30)));

        Assert.Equal("1985-03-14", fields["birthDate"].Normalized);
    }

    [Fact]
    public async Task Rotulo_conhecido_ao_lado_nao_e_devolvido_como_valor()
    {
        // Sem valor abaixo de NOME, o vizinho é outro rótulo: o campo fica NOT_FOUND, não vira "CPF".
        var fields = await ExtractAsync(Stage3Support.Laid(
            new("NOME", 80, 200),
            new("DATA DE NASCIMENTO", 400, 200)));

        Assert.Equal("NOT_FOUND", fields["name"].ValidationStatus);
        Assert.Equal("NOT_FOUND", fields["birthDate"].ValidationStatus);
    }

    [Fact]
    public async Task Evidencia_de_cada_campo_traz_pagina_e_caixa_da_linha()
    {
        var fields = await ExtractAsync(Stage3Support.Laid(
            new("NOME", 80, 200),
            new("MARIA APARECIDA DA SILVA SOUZA", 80, 236, 420, 34)));

        Assert.Equal(1, fields["name"].PageNumber);
        Assert.Equal(8, fields["name"].BoundingBox.Count);
        Assert.Equal(80m, fields["name"].BoundingBox[0]);
        Assert.Equal(236m, fields["name"].BoundingBox[1]);
    }

    [Fact]
    public async Task Ocr_vazio_devolve_todos_os_campos_not_found_sem_inventar_nada()
    {
        var fields = await ExtractAsync(Stage3Support.Of());

        Assert.All(fields.Values, field =>
        {
            Assert.Equal("NOT_FOUND", field.ValidationStatus);
            Assert.Null(field.Normalized);
            Assert.Null(field.Raw);
        });
    }
}
