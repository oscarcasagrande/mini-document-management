using DocReader.Application.Classification;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>Ida e volta entre um perfil e o JSON gravado em <c>DocumentType.classificationRules</c>.</summary>
public sealed class DocumentTypeProfileTests
{
    [Fact]
    public void ToClassificationRulesJson_e_TryParse_sao_inversas()
    {
        var profile = DocumentTypeProfile.BrCpfCard;

        var json = profile.ToClassificationRulesJson();
        var parsed = DocumentTypeProfile.TryParse("BR_CPF_CARD", json, out var reconstructed, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(profile.Threshold, reconstructed!.Threshold);
        Assert.Equal(profile.Evidence.Count, reconstructed.Evidence.Count);
        Assert.Equal(profile.CounterEvidence.Count, reconstructed.CounterEvidence.Count);
        Assert.Equal(profile.Evidence[0].Name, reconstructed.Evidence[0].Name);
        Assert.Equal(profile.Evidence[0].Weight, reconstructed.Evidence[0].Weight);
        Assert.Equal(profile.Evidence[0].Patterns, reconstructed.Evidence[0].Patterns);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"threshold":0.6}""")]
    [InlineData("""{"evidence":[]}""")]
    [InlineData("""{"evidence":[{"name":"a","weight":0.5,"patterns":["A"]}],"threshold":2}""")]
    public void TryParse_recusa_regras_sem_forma_usavel(string json) =>
        Assert.False(DocumentTypeProfile.TryParse("BR_CUSTOM", json, out _, out _));

    [Fact]
    public void TryParse_aceita_contraEvidencia_ausente()
    {
        var ok = DocumentTypeProfile.TryParse(
            "BR_CUSTOM",
            """{"evidence":[{"name":"title","weight":0.7,"patterns":["ALFA"]}],"threshold":0.6}""",
            out var profile,
            out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Empty(profile!.CounterEvidence);
    }
}
