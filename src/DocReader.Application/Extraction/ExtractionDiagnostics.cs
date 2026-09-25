namespace DocReader.Application.Extraction;

/// <summary>
/// O que a extração faria hoje com o OCR gravado de um documento: os blocos que ela recebeu, e para cada campo
/// o resultado, o motivo de ter falhado e o que a regra procurou. Serve para depurar um documento real sem ler o
/// extrator.
/// </summary>
/// <param name="DocumentType">Tipo cujo extrator rodou; null quando não há tipo nem extrator.</param>
/// <param name="DocumentTypeSource">RECORDED (o gravado no processamento) ou CURRENT_CLASSIFICATION (o gravado era UNKNOWN e as regras de agora reconheceram um tipo).</param>
/// <param name="ExtractorVersion">Versão do extrator que rodou agora.</param>
/// <param name="OcrInput">BLOCKS quando há blocos com coordenadas gravados; PAGE_TEXT quando só há o texto (extração anterior à gravação dos blocos), sem geometria.</param>
/// <param name="Pages">Os blocos que o extrator recebeu, por página, na ordem de leitura.</param>
/// <param name="Fields">Um item por campo do tipo.</param>
/// <param name="Coverage">Quantos campos foram lidos.</param>
/// <param name="Note">Aviso sobre o diagnóstico, quando há.</param>
public sealed record ExtractionDiagnostics(
    string? DocumentType,
    string DocumentTypeSource,
    string? ExtractorVersion,
    string OcrInput,
    IReadOnlyList<DiagnosedPage> Pages,
    IReadOnlyList<DiagnosedField> Fields,
    ExtractionCoverage Coverage,
    string? Note);

/// <param name="Page">Página, a partir de 1.</param>
/// <param name="Blocks">Blocos da página.</param>
public sealed record DiagnosedPage(int Page, IReadOnlyList<DiagnosedBlock> Blocks);

/// <param name="Index">Posição na ordem de leitura; é a que os rótulos e candidatos citam.</param>
/// <param name="Text">Texto do bloco.</param>
/// <param name="Confidence">Confiança do OCR.</param>
/// <param name="BoundingBox">Polígono como pares x/y, em pixels da imagem analisada.</param>
public sealed record DiagnosedBlock(int Index, string Text, decimal? Confidence, IReadOnlyList<decimal> BoundingBox);

/// <param name="Path">Caminho do campo no schema.</param>
/// <param name="Status">VALID, INVALID, NOT_FOUND ou UNCERTAIN, com as regras de agora.</param>
/// <param name="Reason">Por que o campo está assim; ver <see cref="ExtractionReasons"/>.</param>
/// <param name="Explanation">O motivo em uma frase.</param>
/// <param name="Messages">Códigos de validação.</param>
/// <param name="Raw">Valor como foi lido.</param>
/// <param name="Normalized">Valor normalizado.</param>
/// <param name="Confidence">Confiança do campo.</param>
/// <param name="Page">Página da evidência.</param>
/// <param name="BoundingBox">Caixa da evidência.</param>
/// <param name="RecordedStatus">Status gravado no processamento; difere de <paramref name="Status"/> depois de uma mudança de regras, até o reprocessamento.</param>
/// <param name="Rule">O que a regra procurou; null quando o campo não usa busca por rótulo.</param>
public sealed record DiagnosedField(
    string Path,
    string Status,
    string Reason,
    string Explanation,
    IReadOnlyList<string> Messages,
    string? Raw,
    string? Normalized,
    decimal? Confidence,
    int? Page,
    IReadOnlyList<decimal> BoundingBox,
    string? RecordedStatus,
    DiagnosedRule? Rule);

/// <param name="Labels">Rótulos procurados e as linhas em que apareceram (lista vazia = não apareceu).</param>
/// <param name="CandidatesSeen">Quantos candidatos a valor a regra examinou.</param>
/// <param name="Candidates">Os primeiros candidatos examinados.</param>
public sealed record DiagnosedRule(
    IReadOnlyList<TracedLabel> Labels,
    int CandidatesSeen,
    IReadOnlyList<DiagnosedCandidate> Candidates);

/// <param name="LineIndex">Linha do texto.</param>
/// <param name="Page">Página.</param>
/// <param name="Text">Texto candidato.</param>
/// <param name="Penalty">Desconto de confiança pela distância ao rótulo.</param>
/// <param name="Accepted">Se o valor lido veio deste candidato.</param>
public sealed record DiagnosedCandidate(int LineIndex, int Page, string Text, decimal Penalty, bool Accepted);

/// <param name="Expected">Campos do tipo (sem os dinâmicos, como sócios de contrato).</param>
/// <param name="Extracted">Campos lidos, válidos ou não: qualquer status que não seja NOT_FOUND.</param>
/// <param name="Valid">Campos lidos e válidos.</param>
public sealed record ExtractionCoverage(int Expected, int Extracted, int Valid)
{
    public decimal ExtractedRatio => Expected == 0 ? 0m : Math.Round((decimal)Extracted / Expected, 4);

    public decimal ValidRatio => Expected == 0 ? 0m : Math.Round((decimal)Valid / Expected, 4);
}

/// <summary>Códigos de motivo de um campo, do mais específico para depurar.</summary>
public static class ExtractionReasons
{
    /// <summary>O campo foi lido.</summary>
    public const string Found = "FOUND";

    /// <summary>Lido, mas sem o rótulo por perto: a confiança é menor.</summary>
    public const string FoundWithoutLabel = "FOUND_WITHOUT_LABEL";

    /// <summary>Nenhum dos rótulos do campo apareceu no texto.</summary>
    public const string LabelNotFound = "LABEL_NOT_FOUND";

    /// <summary>O rótulo apareceu, mas não havia nenhum valor ao lado ou embaixo dele.</summary>
    public const string ValueEmpty = "VALUE_EMPTY";

    /// <summary>O rótulo apareceu e havia valores por perto, mas nenhum tinha o formato esperado.</summary>
    public const string ValueRejected = "VALUE_REJECTED";

    /// <summary>Um valor foi lido e reprovou na validação (dígito verificador, data impossível, formato).</summary>
    public const string ValidationFailed = "VALIDATION_FAILED";

    /// <summary>Um valor foi lido, mas a leitura é uma suposição (ordem de filiação, MRZ incompleta).</summary>
    public const string ValueUncertain = "VALUE_UNCERTAIN";

    /// <summary>A regra do campo não procura por rótulo (MRZ, tipo de serviço, texto corrido) e não achou o padrão.</summary>
    public const string PatternNotFound = "PATTERN_NOT_FOUND";
}
