using DocReader.Application.Classification;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>Regras de pontuação por evidência: soma, limiar, contra-evidência e o override global.</summary>
public sealed class RulesDocumentClassifierTests
{
    private static readonly RulesDocumentClassifier Classifier = new();

    private static ClassificationResult Classify(params string[] lines) => Classifier.Classify(Stage3Support.Of(lines));

    [Fact]
    public void Cartao_de_cpf_completo_e_identificado_com_confianca_maxima()
    {
        var result = Classify(
            "REPUBLICA FEDERATIVA DO BRASIL",
            "MINISTERIO DA FAZENDA",
            "SECRETARIA DA RECEITA FEDERAL",
            "CADASTRO DE PESSOAS FISICAS",
            "NUMERO DE INSCRICAO",
            "NASCIMENTO");

        Assert.Equal("BR_CPF_CARD", result.DocumentType);
        Assert.Equal(0.95m, result.Confidence);
        Assert.Equal(
            ["title", "registration-number", "finance-ministry", "revenue-service", "birth-date"],
            result.Signals);
        Assert.Equal(RulesDocumentClassifier.ClassifierVersion, result.ClassifierVersion);
        Assert.True(result.IsKnown);
    }

    [Fact]
    public void Acento_e_caixa_do_ocr_nao_atrapalham()
    {
        var result = Classify("Cadastro de Pessoas Físicas", "Número de Inscrição");

        Assert.Equal("BR_CPF_CARD", result.DocumentType);
    }

    [Fact]
    public void Titulo_mais_uma_evidencia_de_apoio_atinge_o_limiar()
    {
        var result = Classify("CADASTRO DE PESSOAS FISICAS", "NASCIMENTO");

        Assert.Equal("BR_CPF_CARD", result.DocumentType);
        Assert.Equal(0.6m, result.Confidence);
    }

    [Fact]
    public void So_o_titulo_nao_basta_e_devolve_unknown()
    {
        var result = Classify("CADASTRO DE PESSOAS FISICAS");

        Assert.Equal("UNKNOWN", result.DocumentType);
        Assert.Null(result.Confidence);
        Assert.Empty(result.Signals);
        Assert.False(result.IsKnown);
    }

    [Fact]
    public void Contra_evidencia_subtrai_pontos_mesmo_com_o_resto_presente()
    {
        var result = Classify(
            "CADASTRO DE PESSOAS FISICAS",
            "NUMERO DE INSCRICAO",
            "MINISTERIO DA FAZENDA",
            "CADASTRO NACIONAL DA PESSOA JURIDICA");

        Assert.Equal("UNKNOWN", result.DocumentType);
    }

    [Fact]
    public void Texto_vazio_e_unknown()
    {
        Assert.Equal("UNKNOWN", Classifier.Classify(Stage3Support.Of()).DocumentType);
    }

    [Fact]
    public void Palavras_coladas_pelo_ocr_sao_reconhecidas()
    {
        // O PP-OCRv5 devolve o cabeçalho da CNH sem espaços: é o texto real, não uma hipótese.
        var result = Classify(
            "REPUBLICAFEDERATIVADOBRASIL",
            "MINISTERIODAINFRAESTRUTURA",
            "SECRETARIANACIONALDETRANSITO",
            "CARTEIRA NACIONAL DE HABILITAÇÃO/DRIVER LICENSE/PERMISO DE CONDUCCIÓN");

        Assert.Equal("BR_CNH", result.DocumentType);
    }

    [Fact]
    public void Letra_trocada_pelo_ocr_no_titulo_e_tolerada()
    {
        var result = Classify(
            "CARTEIRA NACIONAL DE HABILITAGAO",
            "SECRETARIA NACIONAL DE TRANSITO",
            "9 CAT. HAB.",
            "4b VALIDADE");

        Assert.Equal("BR_CNH", result.DocumentType);
    }

    [Fact]
    public void Cnh_com_o_titulo_cortado_e_reconhecida_pelos_campos()
    {
        var result = Classify(
            "REPUBLICAFEDERATIVADOBRASIL",
            "SECRETARIANACIONALDETRANSITO",
            "1° HABILITAÇÃO",
            "4c DOC. IDENTIDADE /ÓRG. EMISSOR/UF",
            "4d CPF",
            "5N°REGISTRO",
            "9 CAT. HAB.",
            "4b VALIDADE",
            "FILIAÇÃO");

        Assert.Equal("BR_CNH", result.DocumentType);
        Assert.DoesNotContain("title", result.Signals);
    }

    [Fact]
    public void Registro_de_identidade_civil_e_classificado_como_cin()
    {
        var result = Classify(
            "REPÚBLICA FEDERATIVA DO BRASIL",
            "MINISTÉRIO DA JUSTICA",
            "REGISTRO DE IDENTIDADE CIVIL",
            "DATA DE NASC / DATE OF BIRTH",
            "NÚMERO RIC / ID N",
            "DATA DA EXPEDIÇÃO",
            "FILUAÇÃO",
            "NATURALIDADE",
            "CPF");

        Assert.Equal("BR_CIN", result.DocumentType);
        Assert.Contains("title", result.Signals);
        Assert.Contains("parentage", result.Signals);
    }

    [Fact]
    public void Termo_curto_so_conta_como_palavra_inteira()
    {
        var diagnostics = Classifier.Diagnose("RECEPCAO DE CONVIDADOS E ESPECIFICACAO");

        var proof = diagnostics.Candidates.Single(candidate => candidate.DocumentType == "BR_PROOF_OF_ADDRESS");

        Assert.False(proof.Evidence.Single(evidence => evidence.Name == "postal-code").Matched);
    }

    [Fact]
    public void Limiar_global_substitui_o_de_cada_perfil()
    {
        var lines = new[] { "CADASTRO DE PESSOAS FISICAS" };
        var permissive = new RulesDocumentClassifier(DocumentTypeProfile.All, minimumScore: 0.5m);
        var strict = new RulesDocumentClassifier(DocumentTypeProfile.All, minimumScore: 0.9m);

        Assert.Equal("BR_CPF_CARD", permissive.Classify(Stage3Support.Of(lines)).DocumentType);
        Assert.Equal("UNKNOWN", strict.Classify(Stage3Support.Of("CADASTRO DE PESSOAS FISICAS", "NASCIMENTO")).DocumentType);
    }

    [Fact]
    public void Empate_e_desfeito_pela_ordem_dos_perfis()
    {
        var first = new DocumentTypeProfile("FIRST", [new ClassificationEvidence("e", ["ALFA"], 0.8m)], [], 0.5m);
        var second = new DocumentTypeProfile("SECOND", [new ClassificationEvidence("e", ["ALFA"], 0.8m)], [], 0.5m);

        var result = new RulesDocumentClassifier([first, second]).Classify(Stage3Support.Of("ALFA"));

        Assert.Equal("FIRST", result.DocumentType);
    }

    [Fact]
    public void Pontuacao_e_limitada_a_um()
    {
        var heavy = new DocumentTypeProfile(
            "HEAVY",
            [new ClassificationEvidence("a", ["ALFA"], 0.8m), new ClassificationEvidence("b", ["BETA"], 0.8m)],
            [],
            0.5m);

        var result = new RulesDocumentClassifier([heavy]).Classify(Stage3Support.Of("ALFA", "BETA"));

        Assert.Equal(1.0m, result.Confidence);
    }
}
