using DocReader.Application.Classification;
using Xunit;

namespace DocReader.UnitTests.Application;

/// <summary>Como o matcher trata o que o OCR real faz com uma página.</summary>
public sealed class SearchableTextTests
{
    [Theory]
    [InlineData("REPUBLICAFEDERATIVADOBRASIL", "REPUBLICA FEDERATIVA DO BRASIL")]
    [InlineData("REPUBLICA FEDERATIVA\nDO BRASIL", "REPUBLICA FEDERATIVA DO BRASIL")]
    [InlineData("República   Federativa do Brasil", "REPUBLICA FEDERATIVA DO BRASIL")]
    [InlineData("5N°REGISTRO", "N REGISTRO")]
    public void Espacamento_acento_e_caixa_nao_impedem_o_casamento(string text, string pattern)
    {
        var match = SearchableText.From(text).Find(pattern);

        Assert.NotNull(match);
        Assert.Equal(0, match.Edits);
    }

    [Theory]
    [InlineData("CARTEIRA NACIONAL DE HABILITAGAO", "CARTEIRA NACIONAL DE HABILITACAO", 1)]
    [InlineData("FILUACAO", "FILIACAO", 1)]
    [InlineData("CADASTRO DE PESSOAS FISICA5", "CADASTRO DE PESSOAS FISICAS", 1)]
    public void Erro_de_ocr_e_tolerado_e_contado(string text, string pattern, int edits)
    {
        var match = SearchableText.From(text).Find(pattern);

        Assert.NotNull(match);
        Assert.Equal(edits, match.Edits);
        Assert.Equal("FUZZY", match.Kind);
    }

    [Fact]
    public void Erros_demais_nao_casam()
    {
        Assert.Null(SearchableText.From("CARTEIRA NACIONAL DE HABILITACAO").Find("CARTEIRA DE IDENTIDADE"));
        Assert.Null(SearchableText.From("FILXXCAO").Find("FILIACAO"));
    }

    [Theory]
    [InlineData("CEP 04000-000", true)]
    [InlineData("CEP: 04000000", true)]
    [InlineData("RECEPCAO", false)]
    [InlineData("ACEPIPE", false)]
    public void Termo_curto_exige_palavra_inteira(string text, bool expected)
    {
        Assert.Equal(expected, SearchableText.From(text).Find("CEP") is not null);
    }

    [Fact]
    public void Primeiro_padrao_presente_ganha()
    {
        var match = SearchableText.From("DRIVER LICENSE").FindAny(["CARTEIRA NACIONAL DE HABILITACAO", "DRIVER LICENSE"]);

        Assert.Equal("DRIVER LICENSE", match?.Pattern);
    }

    [Fact]
    public void Texto_vazio_e_padrao_vazio_nao_casam()
    {
        Assert.Null(SearchableText.From(null).Find("CEP"));
        Assert.Null(SearchableText.From("ALGO").Find("   "));
        Assert.Equal(0, SearchableText.From("---").Length);
    }
}
