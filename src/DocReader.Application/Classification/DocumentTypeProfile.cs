namespace DocReader.Application.Classification;

/// <summary>
/// Keyword evidence of one document type. Mirrors <c>x-docreader.classification</c> of the type's
/// JSON Schema; a unit test keeps the two from drifting apart.
///
/// Signals are written without accents and in upper case because the text is normalized the same way
/// before matching.
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
    /// <summary>Cartão / comprovante de inscrição no CPF.</summary>
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

    /// <summary>
    /// CIN e RG. "CARTEIRA DE IDENTIDADE" cobre "CARTEIRA DE IDENTIDADE NACIONAL"; a CNH, que também traz
    /// "IDENTIDADE" no rótulo do documento de origem, nunca traz essa frase inteira.
    /// </summary>
    public static readonly DocumentTypeProfile BrCin = new(
        "BR_CIN",
        RequiredSignals: ["CARTEIRA DE IDENTIDADE"],
        OptionalSignals:
        [
            "REPUBLICA FEDERATIVA DO BRASIL",
            "REGISTRO GERAL",
            "DATA DE NASCIMENTO",
            "FILIACAO",
            "NATURALIDADE",
            "DATA DE EXPEDICAO",
            "CPF"
        ],
        NegativeSignals:
        [
            "CARTEIRA NACIONAL DE HABILITACAO",
            "CADASTRO NACIONAL DA PESSOA JURIDICA",
            "CADASTRO DE PESSOAS FISICAS"
        ],
        Threshold: 0.7m);

    public static readonly DocumentTypeProfile BrCnh = new(
        "BR_CNH",
        RequiredSignals: ["CARTEIRA NACIONAL DE HABILITACAO"],
        OptionalSignals:
        [
            "REPUBLICA FEDERATIVA DO BRASIL",
            "MINISTERIO DOS TRANSPORTES",
            "SECRETARIA NACIONAL DE TRANSITO",
            "CAT. HAB.",
            "VALIDADE",
            "FILIACAO",
            "REGISTRO",
            "CPF"
        ],
        NegativeSignals: ["CADASTRO NACIONAL DA PESSOA JURIDICA", "CERTIFICADO DA CONDICAO DE MICROEMPREENDEDOR INDIVIDUAL"],
        Threshold: 0.7m);

    /// <summary>
    /// Fatura de concessionária. Não há uma frase que todas tragam, então o CEP é o sinal obrigatório e
    /// o serviço (energia, água, gás, telefonia) e os termos de cobrança sobem a confiança. Os sinais
    /// negativos afastam os documentos cadastrais e contratuais, que também têm endereço e CEP.
    /// </summary>
    public static readonly DocumentTypeProfile BrProofOfAddress = new(
        "BR_PROOF_OF_ADDRESS",
        RequiredSignals: ["CEP"],
        OptionalSignals:
        [
            "FATURA",
            "NOTA FISCAL",
            "ENERGIA ELETRICA",
            "AGUA E ESGOTO",
            "GAS NATURAL",
            "TELEFONE",
            "INTERNET",
            "UNIDADE CONSUMIDORA",
            "VENCIMENTO",
            "TOTAL A PAGAR",
            "CONSUMO",
            "REFERENCIA"
        ],
        NegativeSignals:
        [
            "CADASTRO NACIONAL DA PESSOA JURIDICA",
            "CERTIFICADO DA CONDICAO DE MICROEMPREENDEDOR INDIVIDUAL",
            "CARTEIRA NACIONAL DE HABILITACAO",
            "CARTEIRA DE IDENTIDADE",
            "CLAUSULA"
        ],
        Threshold: 0.7m);

    public static readonly DocumentTypeProfile BrCnpjCard = new(
        "BR_CNPJ_CARD",
        RequiredSignals: ["CADASTRO NACIONAL DA PESSOA JURIDICA"],
        OptionalSignals:
        [
            "COMPROVANTE DE INSCRICAO E DE SITUACAO CADASTRAL",
            "NUMERO DE INSCRICAO",
            "DATA DE ABERTURA",
            "NOME EMPRESARIAL",
            "NATUREZA JURIDICA",
            "SITUACAO CADASTRAL",
            "ATIVIDADE ECONOMICA PRINCIPAL"
        ],
        NegativeSignals: ["CERTIFICADO DA CONDICAO DE MICROEMPREENDEDOR INDIVIDUAL", "CLAUSULA"],
        Threshold: 0.7m);

    public static readonly DocumentTypeProfile BrCcmei = new(
        "BR_CCMEI",
        RequiredSignals: ["CERTIFICADO DA CONDICAO DE MICROEMPREENDEDOR INDIVIDUAL"],
        OptionalSignals:
        [
            "CCMEI",
            "PORTAL DO EMPREENDEDOR",
            "NOME EMPRESARIAL",
            "NOME FANTASIA",
            "CAPITAL SOCIAL",
            "OCUPACAO PRINCIPAL",
            "ENDERECO COMERCIAL"
        ],
        NegativeSignals: ["CLAUSULA"],
        Threshold: 0.7m);

    /// <summary>
    /// Contrato social e suas alterações. "CLAUSULA" e "QUOTAS" são o que separa o instrumento de um
    /// cartão ou comprovante que só cita o CNPJ da empresa.
    /// </summary>
    public static readonly DocumentTypeProfile BrSocialContract = new(
        "BR_SOCIAL_CONTRACT",
        RequiredSignals: ["CONTRATO SOCIAL"],
        OptionalSignals:
        [
            "CLAUSULA",
            "SOCIOS",
            "QUOTAS",
            "CAPITAL SOCIAL",
            "OBJETO SOCIAL",
            "SEDE",
            "ADMINISTRACAO",
            "FORO",
            "SOCIEDADE"
        ],
        NegativeSignals:
        [
            "COMPROVANTE DE INSCRICAO E DE SITUACAO CADASTRAL",
            "CERTIFICADO DA CONDICAO DE MICROEMPREENDEDOR INDIVIDUAL"
        ],
        Threshold: 0.7m);

    /// <summary>Todos os perfis do classificador de regras, na ordem de desempate.</summary>
    public static IReadOnlyList<DocumentTypeProfile> All { get; } =
    [
        BrCpfCard, BrCin, BrCnh, BrCnpjCard, BrCcmei, BrSocialContract, BrProofOfAddress
    ];
}
