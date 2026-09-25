namespace DocReader.Application.Classification;

/// <summary>
/// Evidence of one document type. Mirrors <c>x-docreader.classification</c> of the type's JSON Schema; a
/// unit test keeps the two from drifting apart.
///
/// The score is the sum of the weights of the evidence found, minus the penalties of the counter-evidence
/// found, kept between 0 and 1. The type is accepted at or above its threshold. No single evidence is
/// mandatory: a scan with the title cropped can still be recognised by the fields around it, while the
/// title alone is deliberately not enough. Patterns are written without accents and in upper case.
/// </summary>
/// <param name="DocumentType">Type name, as in the schema file.</param>
/// <param name="Evidence">What raises the score.</param>
/// <param name="CounterEvidence">What belongs to a different document and lowers the score.</param>
/// <param name="Threshold">Minimum score to accept the type.</param>
public sealed record DocumentTypeProfile(
    string DocumentType,
    IReadOnlyList<ClassificationEvidence> Evidence,
    IReadOnlyList<ClassificationEvidence> CounterEvidence,
    decimal Threshold)
{
    private const decimal DefaultThreshold = 0.6m;

    private const string CnpjTitle = "CADASTRO NACIONAL DA PESSOA JURIDICA";
    private const string CnhTitle = "CARTEIRA NACIONAL DE HABILITACAO";
    private const string CcmeiTitle = "CERTIFICADO DA CONDICAO DE MICROEMPREENDEDOR INDIVIDUAL";
    private const string CpfTitle = "CADASTRO DE PESSOAS FISICAS";
    private const string CnpjReceiptTitle = "COMPROVANTE DE INSCRICAO E DE SITUACAO CADASTRAL";

    /// <summary>Cartão / comprovante de inscrição no CPF.</summary>
    public static readonly DocumentTypeProfile BrCpfCard = new(
        "BR_CPF_CARD",
        Evidence:
        [
            Of("title", 0.50m, CpfTitle),
            Of("registration-number", 0.15m, "NUMERO DE INSCRICAO", "N DE INSCRICAO"),
            Of("finance-ministry", 0.10m, "MINISTERIO DA FAZENDA"),
            Of("revenue-service", 0.10m, "SECRETARIA DA RECEITA FEDERAL", "RECEITA FEDERAL"),
            Of("birth-date", 0.10m, "DATA DE NASCIMENTO", "NASCIMENTO"),
            Of("cpf", 0.05m, "CPF")
        ],
        CounterEvidence:
        [
            Of("cnpj-title", 0.60m, CnpjTitle),
            Of("cnh-title", 0.60m, CnhTitle)
        ],
        Threshold: DefaultThreshold);

    /// <summary>
    /// CIN e RG, incluindo o Registro de Identidade Civil (RIC), que traz o título "REGISTRO DE IDENTIDADE
    /// CIVIL" e não "CARTEIRA DE IDENTIDADE". A CNH, que também tem "IDENTIDADE" no rótulo do documento de
    /// origem, nunca traz essas frases inteiras.
    /// </summary>
    public static readonly DocumentTypeProfile BrCin = new(
        "BR_CIN",
        Evidence:
        [
            Of("title", 0.45m, "CARTEIRA DE IDENTIDADE", "REGISTRO DE IDENTIDADE CIVIL", "CEDULA DE IDENTIDADE"),
            Of("republic", 0.10m, "REPUBLICA FEDERATIVA DO BRASIL"),
            Of("registration-number", 0.10m, "REGISTRO GERAL", "NUMERO RIC", "NUMERO DO REGISTRO GERAL"),
            Of("birth-date", 0.10m, "DATA DE NASCIMENTO", "DATA DE NASC", "DATE OF BIRTH"),
            Of("parentage", 0.10m, "FILIACAO"),
            Of("place-of-birth", 0.05m, "NATURALIDADE"),
            Of("issue-date", 0.05m, "DATA DE EXPEDICAO", "DATA DA EXPEDICAO"),
            Of("issuing-body", 0.05m, "ORGAO EMISSOR", "ORGAO EXPEDIDOR"),
            Of("cpf", 0.05m, "CPF")
        ],
        CounterEvidence:
        [
            Of("cnh-title", 0.60m, CnhTitle),
            Of("cnpj-title", 0.60m, CnpjTitle),
            Of("cpf-title", 0.60m, CpfTitle)
        ],
        Threshold: DefaultThreshold);

    /// <summary>
    /// CNH em qualquer geração do layout: o título vem em português ou no cabeçalho trilíngue, o órgão
    /// emissor mudou de nome (Denatran, Senatran, Ministério dos Transportes, da Infraestrutura, das
    /// Cidades) e os rótulos de campo trazem número e abreviação ("5 Nº REGISTRO", "9 CAT. HAB.").
    /// </summary>
    public static readonly DocumentTypeProfile BrCnh = new(
        "BR_CNH",
        Evidence:
        [
            Of("title", 0.45m, CnhTitle, "DRIVER LICENSE", "PERMISO DE CONDUCCION"),
            Of("republic", 0.05m, "REPUBLICA FEDERATIVA DO BRASIL"),
            Of(
                "issuing-authority",
                0.15m,
                "SECRETARIA NACIONAL DE TRANSITO",
                "SENATRAN",
                "DENATRAN",
                "MINISTERIO DOS TRANSPORTES",
                "MINISTERIO DA INFRAESTRUTURA",
                "MINISTERIO DAS CIDADES"),
            Of("category", 0.10m, "CAT HAB", "CATEGORIA DE HABILITACAO"),
            Of("validity", 0.05m, "VALIDADE"),
            Of("parentage", 0.05m, "FILIACAO"),
            Of("registration-number", 0.05m, "N REGISTRO", "NUMERO DE REGISTRO", "REGISTRO"),
            Of("identity-document", 0.05m, "DOC IDENTIDADE", "ORG EMISSOR"),
            Of("first-license", 0.05m, "1 HABILITACAO", "PRIMEIRA HABILITACAO"),
            Of("cpf", 0.05m, "CPF")
        ],
        CounterEvidence:
        [
            Of("cnpj-title", 0.60m, CnpjTitle),
            Of("ccmei-title", 0.60m, CcmeiTitle)
        ],
        Threshold: DefaultThreshold);

    /// <summary>
    /// Fatura de concessionária. Não há uma frase que todas tragam, então nenhum termo é obrigatório: o
    /// CEP, o tipo de serviço (energia, água, gás, telefonia), os campos de cobrança e o código do cliente
    /// somam. O CEP sozinho fica muito abaixo do limiar, e os documentos cadastrais e contratuais, que
    /// também têm endereço, CEP e às vezes vencimento, são afastados pela contra-evidência.
    /// </summary>
    public static readonly DocumentTypeProfile BrProofOfAddress = new(
        "BR_PROOF_OF_ADDRESS",
        Evidence:
        [
            Of("postal-code", 0.15m, "CEP"),
            Of("bill", 0.15m, "FATURA", "NOTA FISCAL", "CONTA DE ENERGIA", "CONTA DE AGUA", "BOLETO", "DEMONSTRATIVO"),
            Of(
                "utility",
                0.20m,
                "ENERGIA ELETRICA",
                "AGUA E ESGOTO",
                "GAS NATURAL",
                "TELEFONIA",
                "TELEFONE",
                "INTERNET",
                "SANEAMENTO",
                "DISTRIBUIDORA"),
            Of(
                "customer-code",
                0.15m,
                "UNIDADE CONSUMIDORA",
                "CODIGO DO CLIENTE",
                "NUMERO DO CLIENTE",
                "N DO CLIENTE",
                "INSTALACAO"),
            Of("due-date", 0.15m, "VENCIMENTO", "DATA DE VENCIMENTO"),
            Of("amount-due", 0.15m, "TOTAL A PAGAR", "VALOR A PAGAR", "VALOR TOTAL", "VALOR DO DOCUMENTO"),
            Of("consumption", 0.05m, "CONSUMO", "LEITURA"),
            Of("reference-period", 0.05m, "MES DE REFERENCIA", "REFERENCIA", "COMPETENCIA")
        ],
        CounterEvidence:
        [
            Of("cnpj-title", 0.60m, CnpjTitle),
            Of("ccmei-title", 0.60m, CcmeiTitle),
            Of("cnh-title", 0.60m, CnhTitle),
            Of("identity-title", 0.60m, "CARTEIRA DE IDENTIDADE", "REGISTRO DE IDENTIDADE CIVIL"),
            Of("contract-clause", 0.60m, "CLAUSULA")
        ],
        Threshold: DefaultThreshold);

    public static readonly DocumentTypeProfile BrCnpjCard = new(
        "BR_CNPJ_CARD",
        Evidence:
        [
            Of("title", 0.40m, CnpjTitle, "CADASTRO NACIONAL DE PESSOA JURIDICA"),
            Of("receipt-title", 0.20m, CnpjReceiptTitle),
            Of("registration-number", 0.10m, "NUMERO DE INSCRICAO"),
            Of("opening-date", 0.10m, "DATA DE ABERTURA"),
            Of("company-name", 0.10m, "NOME EMPRESARIAL"),
            Of("legal-nature", 0.05m, "NATUREZA JURIDICA"),
            Of("registration-status", 0.05m, "SITUACAO CADASTRAL"),
            Of("main-activity", 0.05m, "ATIVIDADE ECONOMICA PRINCIPAL"),
            Of("republic", 0.05m, "REPUBLICA FEDERATIVA DO BRASIL")
        ],
        CounterEvidence:
        [
            Of("ccmei-title", 0.60m, CcmeiTitle),
            Of("contract-clause", 0.40m, "CLAUSULA")
        ],
        Threshold: DefaultThreshold);

    public static readonly DocumentTypeProfile BrCcmei = new(
        "BR_CCMEI",
        Evidence:
        [
            Of("title", 0.40m, CcmeiTitle),
            Of("micro-entrepreneur", 0.20m, "MICROEMPREENDEDOR INDIVIDUAL"),
            Of("acronym", 0.10m, "CCMEI"),
            Of("portal", 0.10m, "PORTAL DO EMPREENDEDOR"),
            Of("company-name", 0.05m, "NOME EMPRESARIAL"),
            Of("trade-name", 0.05m, "NOME FANTASIA"),
            Of("share-capital", 0.05m, "CAPITAL SOCIAL"),
            Of("main-occupation", 0.05m, "OCUPACAO PRINCIPAL"),
            Of("business-address", 0.05m, "ENDERECO COMERCIAL")
        ],
        CounterEvidence: [Of("contract-clause", 0.40m, "CLAUSULA")],
        Threshold: DefaultThreshold);

    /// <summary>
    /// Contrato social e suas alterações. "CLAUSULA" e "QUOTAS" são o que separa o instrumento de um
    /// cartão ou comprovante que só cita o CNPJ da empresa.
    /// </summary>
    public static readonly DocumentTypeProfile BrSocialContract = new(
        "BR_SOCIAL_CONTRACT",
        Evidence:
        [
            Of(
                "title",
                0.35m,
                "CONTRATO SOCIAL",
                "ALTERACAO CONTRATUAL",
                "CONSOLIDACAO CONTRATUAL",
                "INSTRUMENTO PARTICULAR DE CONSTITUICAO"),
            Of("clause", 0.20m, "CLAUSULA"),
            Of("partners", 0.10m, "SOCIOS", "SOCIO", "SOCIA"),
            Of("quotas", 0.10m, "QUOTAS", "QUOTA", "COTAS"),
            Of("share-capital", 0.10m, "CAPITAL SOCIAL"),
            Of("corporate-purpose", 0.05m, "OBJETO SOCIAL"),
            Of("registered-office", 0.05m, "SEDE"),
            Of("administration", 0.05m, "ADMINISTRACAO"),
            Of("jurisdiction", 0.05m, "FORO"),
            Of("company-type", 0.05m, "SOCIEDADE", "LTDA", "EIRELI")
        ],
        CounterEvidence:
        [
            Of("cnpj-receipt-title", 0.60m, CnpjReceiptTitle),
            Of("ccmei-title", 0.60m, CcmeiTitle)
        ],
        Threshold: DefaultThreshold);

    /// <summary>Todos os perfis do classificador de regras, na ordem de desempate.</summary>
    public static IReadOnlyList<DocumentTypeProfile> All { get; } =
    [
        BrCpfCard, BrCin, BrCnh, BrCnpjCard, BrCcmei, BrSocialContract, BrProofOfAddress
    ];

    private static ClassificationEvidence Of(string name, decimal weight, params string[] patterns) =>
        new(name, patterns, weight);
}
