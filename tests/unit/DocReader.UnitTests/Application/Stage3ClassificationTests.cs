using System.Text.Json;
using DocReader.Application.Classification;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>Classificação dos tipos da Etapa 3 e a paridade entre cada perfil e o seu JSON Schema.</summary>
public sealed class Stage3ClassificationTests
{
    private static readonly RulesDocumentClassifier Classifier = new();

    private static string Classify(params string[] lines) => Classifier.Classify(Stage3Support.Of(lines)).DocumentType;

    [Fact]
    public void Cin_e_identificada_pela_carteira_de_identidade()
    {
        Assert.Equal("BR_CIN", Classify(
            "REPÚBLICA FEDERATIVA DO BRASIL", "CARTEIRA DE IDENTIDADE NACIONAL", "REGISTRO GERAL", "DATA DE NASCIMENTO", "CPF"));
    }

    [Fact]
    public void Rg_antigo_cai_no_mesmo_tipo_da_cin()
    {
        Assert.Equal("BR_CIN", Classify("CARTEIRA DE IDENTIDADE", "REGISTRO GERAL", "FILIAÇÃO", "NATURALIDADE"));
    }

    [Fact]
    public void Cnh_nao_e_confundida_com_cin_mesmo_com_documento_de_identidade_no_rotulo()
    {
        var text = new[]
        {
            "REPÚBLICA FEDERATIVA DO BRASIL", "CARTEIRA NACIONAL DE HABILITAÇÃO", "DOC. IDENTIDADE / ORG. EMISSOR / UF",
            "CPF", "5 Nº REGISTRO", "9 CAT. HAB.", "4b VALIDADE"
        };

        Assert.Equal("BR_CNH", Classify(text));
    }

    [Fact]
    public void Comprovante_de_residencia_e_identificado_pelo_cep_e_pelos_termos_de_cobranca()
    {
        Assert.Equal("BR_PROOF_OF_ADDRESS", Classify(
            "COMPANHIA ENERGÉTICA EXEMPLO S.A.", "FATURA DE ENERGIA ELÉTRICA", "CEP 04000-000 SÃO PAULO - SP",
            "UNIDADE CONSUMIDORA", "VENCIMENTO", "TOTAL A PAGAR"));
    }

    [Fact]
    public void Cep_sozinho_nao_basta_para_comprovante_de_residencia()
    {
        Assert.Equal("UNKNOWN", Classify("CEP 04000-000", "ENDEREÇO"));
    }

    [Fact]
    public void Cartao_cnpj_tem_endereco_e_cep_mas_nao_e_comprovante_de_residencia()
    {
        Assert.Equal("BR_CNPJ_CARD", Classify(
            "REPÚBLICA FEDERATIVA DO BRASIL", "CADASTRO NACIONAL DA PESSOA JURÍDICA",
            "COMPROVANTE DE INSCRIÇÃO E DE SITUAÇÃO CADASTRAL", "NÚMERO DE INSCRIÇÃO", "DATA DE ABERTURA",
            "NOME EMPRESARIAL", "LOGRADOURO", "CEP", "SITUAÇÃO CADASTRAL"));
    }

    [Fact]
    public void Ccmei_e_identificado_e_nao_vira_cartao_cnpj_nem_comprovante()
    {
        Assert.Equal("BR_CCMEI", Classify(
            "CCMEI", "CERTIFICADO DA CONDIÇÃO DE MICROEMPREENDEDOR INDIVIDUAL", "NOME EMPRESARIAL", "CNPJ",
            "ENDEREÇO COMERCIAL", "CEP 01000-000", "CAPITAL SOCIAL"));
    }

    [Fact]
    public void Contrato_social_e_identificado_pelas_clausulas()
    {
        Assert.Equal("BR_SOCIAL_CONTRACT", Classify(
            "INSTRUMENTO PARTICULAR DE CONTRATO SOCIAL", "CLÁUSULA PRIMEIRA - DA DENOMINAÇÃO E DA SEDE",
            "com sede na Rua das Amostras, CEP 01000-000", "CAPITAL SOCIAL", "QUOTAS", "FORO"));
    }

    [Fact]
    public void Contrato_com_endereco_e_cep_nao_vira_comprovante_de_residencia()
    {
        Assert.Equal("BR_SOCIAL_CONTRACT", Classify(
            "CONTRATO SOCIAL", "CLÁUSULA PRIMEIRA", "CEP 01000-000", "VENCIMENTO", "CAPITAL SOCIAL", "QUOTAS"));
    }

    [Fact]
    public void Cpf_continua_sendo_identificado_com_todos_os_perfis_ligados()
    {
        Assert.Equal("BR_CPF_CARD", Classify(
            "CADASTRO DE PESSOAS FISICAS", "NUMERO DE INSCRICAO", "MINISTERIO DA FAZENDA", "NASCIMENTO"));
    }

    [Fact]
    public void Texto_sem_evidencia_e_unknown()
    {
        Assert.Equal("UNKNOWN", Classify("Bom dia, segue em anexo o relatório mensal."));
    }

    [Fact]
    public void Nenhum_perfil_repete_nome_de_tipo()
    {
        var names = DocumentTypeProfile.All.Select(profile => profile.DocumentType).ToArray();

        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.Equal(7, names.Length);
    }

    [Theory]
    [InlineData("BR_CPF_CARD")]
    [InlineData("BR_CIN")]
    [InlineData("BR_CNH")]
    [InlineData("BR_PROOF_OF_ADDRESS")]
    [InlineData("BR_CNPJ_CARD")]
    [InlineData("BR_CCMEI")]
    [InlineData("BR_SOCIAL_CONTRACT")]
    public void Perfil_no_codigo_espelha_os_sinais_do_schema_json(string documentType)
    {
        var schemaPath = Path.Combine(RepositoryRoot(), "schemas", "documents", $"{documentType}.v1.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));

        Assert.Equal($"{documentType}.v1", schema.RootElement.GetProperty("$id").GetString());

        var classification = schema.RootElement.GetProperty("x-docreader").GetProperty("classification");

        var profile = DocumentTypeProfile.All.Single(candidate => candidate.DocumentType == documentType);

        Assert.Equal(classification.GetProperty("threshold").GetDecimal(), profile.Threshold);
        AssertSameEvidence(classification.GetProperty("evidence"), profile.Evidence);
        AssertSameEvidence(classification.GetProperty("counterEvidence"), profile.CounterEvidence);
    }

    private static void AssertSameEvidence(JsonElement schemaEvidence, IReadOnlyList<ClassificationEvidence> profileEvidence)
    {
        var fromSchema = schemaEvidence.EnumerateArray()
            .Select(item => (
                Name: item.GetProperty("name").GetString()!,
                Weight: item.GetProperty("weight").GetDecimal(),
                Patterns: item.GetProperty("patterns").EnumerateArray().Select(pattern => pattern.GetString()!).ToArray()))
            .ToArray();

        Assert.Equal(profileEvidence.Count, fromSchema.Length);

        for (var index = 0; index < fromSchema.Length; index++)
        {
            Assert.Equal(profileEvidence[index].Name, fromSchema[index].Name);
            Assert.Equal(profileEvidence[index].Weight, fromSchema[index].Weight);
            Assert.Equal(profileEvidence[index].Patterns, fromSchema[index].Patterns);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocReader.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("DocReader.slnx not found above the test binaries.");
    }
}
