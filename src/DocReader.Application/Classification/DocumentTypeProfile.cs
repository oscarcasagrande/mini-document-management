namespace DocReader.Application.Classification;

/// <summary>
/// Keyword evidence of one document type. Mirrors <c>x-docreader.classification</c> of the type's
/// JSON Schema; a unit test keeps the two from drifting apart.
/// </summary>
/// <param name="DocumentType">Type name, as in the schema file.</param>
/// <param name="RequiredSignals">All of them must appear, otherwise the type is discarded.</param>
/// <param name="OptionalSignals">Each one found raises the confidence.</param>
/// <param name="NegativeSignals">Any of them discards the type: it belongs to a different document.</param>
/// <param name="Threshold">Minimum score to accept the type.</param>
public sealed record DocumentTypeProfile(
    string DocumentType,
    IReadOnlyList<string> RequiredSignals,
    IReadOnlyList<string> OptionalSignals,
    IReadOnlyList<string> NegativeSignals,
    decimal Threshold)
{
    /// <summary>
    /// The CPF card. Signals are written without accents and in upper case because the text is
    /// normalized the same way before matching.
    /// </summary>
    public static readonly DocumentTypeProfile BrCpfCard = new(
        "BR_CPF_CARD",
        RequiredSignals: ["CADASTRO DE PESSOAS FISICAS"],
        OptionalSignals:
        [
            "NUMERO DE INSCRICAO",
            "MINISTERIO DA FAZENDA",
            "SECRETARIA DA RECEITA FEDERAL",
            "NASCIMENTO"
        ],
        NegativeSignals: ["CADASTRO NACIONAL DA PESSOA JURIDICA", "CARTEIRA NACIONAL DE HABILITACAO"],
        Threshold: 0.7m);
}
