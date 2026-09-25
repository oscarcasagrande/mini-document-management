using DocReader.Application.Classification;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>O diagnóstico tem de explicar a decisão e nunca divergir dela.</summary>
public sealed class ClassificationDiagnosticsTests
{
    private static readonly RulesDocumentClassifier Classifier = new();

    [Fact]
    public void Diagnostico_traz_um_candidato_por_perfil_com_o_melhor_primeiro()
    {
        var diagnostics = Classifier.Diagnose("CADASTRO DE PESSOAS FISICAS\nNUMERO DE INSCRICAO\nNASCIMENTO");

        Assert.Equal(DocumentTypeProfile.All.Count, diagnostics.Candidates.Count);
        Assert.Equal("BR_CPF_CARD", diagnostics.Candidates[0].DocumentType);
        Assert.Equal(
            diagnostics.Candidates.OrderByDescending(candidate => candidate.Score).Select(candidate => candidate.Score),
            diagnostics.Candidates.Select(candidate => candidate.Score));
    }

    [Fact]
    public void Diagnostico_e_a_decisao_gravada_sao_o_mesmo_resultado()
    {
        var lines = new[] { "CARTEIRA NACIONAL DE HABILITAÇÃO", "SECRETARIA NACIONAL DE TRANSITO", "9 CAT. HAB." };

        var diagnostics = Classifier.Diagnose(string.Join('\n', lines));
        var classification = Classifier.Classify(Stage3Support.Of(lines));

        Assert.Equal(classification.DocumentType, diagnostics.DocumentType);
        Assert.Equal(classification.Confidence, diagnostics.Confidence);
    }

    [Fact]
    public void Unknown_explica_o_tipo_mais_proximo_e_quanto_faltou()
    {
        var diagnostics = Classifier.Diagnose("CADASTRO DE PESSOAS FISICAS");

        Assert.Equal("UNKNOWN", diagnostics.DocumentType);
        Assert.Null(diagnostics.Confidence);
        Assert.Contains("No type reached its threshold", diagnostics.Reason, StringComparison.Ordinal);
        Assert.Contains("BR_CPF_CARD", diagnostics.Reason, StringComparison.Ordinal);
        Assert.Contains("0.50 of 0.60", diagnostics.Reason, StringComparison.Ordinal);

        var closest = diagnostics.Candidates[0];
        Assert.False(closest.Accepted);
        Assert.Contains("below the threshold 0.60 (short by 0.10)", closest.Reason, StringComparison.Ordinal);
        Assert.Contains("Not found (worth up to 0.50)", closest.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Texto_vazio_e_dito_como_vazio()
    {
        var diagnostics = Classifier.Diagnose("   ");

        Assert.Equal("UNKNOWN", diagnostics.DocumentType);
        Assert.Equal(0, diagnostics.TextLength);
        Assert.Contains("empty", diagnostics.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Texto_sem_nenhuma_evidencia_e_dito_sem_evidencia()
    {
        var diagnostics = Classifier.Diagnose("Bom dia, segue em anexo o relatorio mensal.");

        Assert.Contains("No evidence of any known type", diagnostics.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidencia_achada_diz_qual_padrao_casou_e_se_precisou_tolerar_erro()
    {
        var diagnostics = Classifier.Diagnose("REGISTRO DE IDENTIDADE CIVIL\nFILUAÇÃO\nNATURALIDADE");

        var cin = diagnostics.Candidates.Single(candidate => candidate.DocumentType == "BR_CIN");
        var title = cin.Evidence.Single(evidence => evidence.Name == "title");
        var parentage = cin.Evidence.Single(evidence => evidence.Name == "parentage");
        var placeOfBirth = cin.Evidence.Single(evidence => evidence.Name == "place-of-birth");

        Assert.True(title.Matched);
        Assert.Equal("REGISTRO DE IDENTIDADE CIVIL", title.MatchedPattern);
        Assert.Equal("EXACT", title.MatchKind);
        Assert.Equal(0, title.Edits);

        Assert.True(parentage.Matched);
        Assert.Equal("FUZZY", parentage.MatchKind);
        Assert.Equal(1, parentage.Edits);

        Assert.True(placeOfBirth.Matched);
    }

    [Fact]
    public void Contra_evidencia_achada_aparece_na_razao_com_a_penalidade()
    {
        var diagnostics = Classifier.Diagnose(
            "CADASTRO DE PESSOAS FISICAS\nNUMERO DE INSCRICAO\nCADASTRO NACIONAL DA PESSOA JURIDICA");

        var cpf = diagnostics.Candidates.Single(candidate => candidate.DocumentType == "BR_CPF_CARD");

        Assert.Contains("Counter-evidence lowered the score: cnpj-title (-0.60)", cpf.Reason, StringComparison.Ordinal);
        Assert.True(cpf.CounterEvidence.Single(evidence => evidence.Name == "cnpj-title").Matched);
    }

    [Fact]
    public void Candidato_aceito_que_perdeu_diz_quem_ganhou()
    {
        var strong = new DocumentTypeProfile(
            "STRONG",
            [new ClassificationEvidence("a", ["ALFA"], 0.5m), new ClassificationEvidence("b", ["BETA"], 0.4m)],
            [],
            0.6m);
        var weak = new DocumentTypeProfile("WEAK", [new ClassificationEvidence("a", ["ALFA"], 0.5m)], [], 0.6m);

        var diagnostics = new RulesDocumentClassifier([weak, strong], minimumScore: 0.4m).Diagnose("ALFA BETA");

        Assert.Equal("STRONG", diagnostics.DocumentType);
        Assert.Equal(["STRONG", "WEAK"], diagnostics.Candidates.Select(candidate => candidate.DocumentType));
        Assert.All(diagnostics.Candidates, candidate => Assert.True(candidate.Accepted));
        Assert.Contains("Not chosen: STRONG scored higher (0.90)", diagnostics.Candidates[1].Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Not chosen", diagnostics.Candidates[0].Reason, StringComparison.Ordinal);
        Assert.Equal(0.4m, diagnostics.ThresholdOverride);
    }

    [Fact]
    public void Sem_override_o_diagnostico_nao_reporta_limiar_global()
    {
        Assert.Null(Classifier.Diagnose("qualquer coisa").ThresholdOverride);
    }

    [Fact]
    public void Diagnostico_nao_carrega_conteudo_do_documento()
    {
        const string name = "MARIA APARECIDA DA SILVA SOUZA";

        var diagnostics = Classifier.Diagnose("CADASTRO DE PESSOAS FISICAS\n" + name + "\nNASCIMENTO");

        var everything = string.Join(
            '\n',
            diagnostics.Candidates.SelectMany(candidate => candidate.Evidence
                .Select(evidence => evidence.MatchedPattern ?? string.Empty)
                .Append(candidate.Reason)));

        Assert.DoesNotContain("MARIA", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("SOUZA", diagnostics.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Todo_perfil_pode_chegar_ao_limiar_e_o_titulo_sozinho_e_insuficiente_nos_ambiguos()
    {
        foreach (var profile in DocumentTypeProfile.All)
        {
            Assert.True(profile.Evidence.Sum(item => item.Weight) >= 1m, profile.DocumentType);
            Assert.True(profile.Threshold is > 0m and <= 1m, profile.DocumentType);
            Assert.Equal(profile.Evidence.Count, profile.Evidence.Select(item => item.Name).Distinct().Count());
            Assert.All(profile.Evidence.Concat(profile.CounterEvidence), item =>
            {
                Assert.NotEmpty(item.Patterns);
                Assert.True(item.Weight is > 0m and <= 1m);
            });
        }

        foreach (var ambiguous in new[] { DocumentTypeProfile.BrCpfCard, DocumentTypeProfile.BrCin, DocumentTypeProfile.BrCnh })
        {
            var title = ambiguous.Evidence.Single(item => item.Name == "title");
            Assert.True(title.Weight < ambiguous.Threshold, ambiguous.DocumentType);
        }
    }
}
