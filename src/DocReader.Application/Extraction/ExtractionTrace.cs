namespace DocReader.Application.Extraction;

/// <summary>
/// O que uma extração tentou, para explicar um campo que falhou sem ler o código do extrator. Cada campo (ou
/// grupo de campos que nascem da mesma regra) roda dentro de um escopo; o <see cref="LineSearch"/> registra nele os
/// rótulos procurados, as linhas em que o rótulo apareceu e os candidatos a valor examinados.
///
/// Só existe quando alguém pede o diagnóstico: a extração normal passa <c>null</c> e não paga nada.
/// </summary>
public sealed class ExtractionTrace
{
    /// <summary>Candidatos guardados por escopo; o resto só é contado.</summary>
    public const int MaximumCandidates = 12;

    private readonly List<FieldScope> _scopes = [];
    private FieldScope? _current;

    public IReadOnlyList<FieldScope> Scopes => _scopes;

    /// <summary>O escopo que produziu o campo, ou null quando a regra do campo não usa busca por rótulo.</summary>
    public FieldScope? ScopeOf(string field) => _scopes.FirstOrDefault(scope => scope.Fields.Contains(field));

    internal T Run<T>(IReadOnlyList<string> fields, Func<T> read)
    {
        var scope = new FieldScope(fields);
        _scopes.Add(scope);

        var previous = _current;
        _current = scope;
        try
        {
            return read();
        }
        finally
        {
            _current = previous;
        }
    }

    internal void LabelSearched(string label, IReadOnlyList<int> lineIndexes) => _current?.AddLabel(label, lineIndexes);

    internal void CandidateSeen(OcrTextLine line, string text, decimal penalty) => _current?.AddCandidate(line, text, penalty);
}

/// <summary>Uma regra de extração e o que ela viu.</summary>
public sealed class FieldScope(IReadOnlyList<string> fields)
{
    private readonly Dictionary<string, HashSet<int>> _labels = new(StringComparer.Ordinal);
    private readonly List<TracedCandidate> _candidates = [];
    private readonly HashSet<(int, string)> _seen = [];

    public IReadOnlyList<string> Fields { get; } = fields;

    /// <summary>Rótulos procurados, cada um com as linhas (índice de leitura) onde apareceu.</summary>
    public IReadOnlyList<TracedLabel> Labels =>
        [.. _labels.Select(pair => new TracedLabel(pair.Key, [.. pair.Value.Order()]))];

    /// <summary>Candidatos a valor examinados, na ordem em que a regra os viu (até <see cref="ExtractionTrace.MaximumCandidates"/>).</summary>
    public IReadOnlyList<TracedCandidate> Candidates => _candidates;

    /// <summary>Quantos candidatos distintos a regra examinou, contados os que passaram do limite guardado.</summary>
    public int CandidateCount { get; private set; }

    internal void AddLabel(string label, IReadOnlyList<int> lineIndexes)
    {
        if (!_labels.TryGetValue(label, out var known))
        {
            known = [];
            _labels[label] = known;
        }

        foreach (var index in lineIndexes)
        {
            known.Add(index);
        }
    }

    internal void AddCandidate(OcrTextLine line, string text, decimal penalty)
    {
        if (!_seen.Add((line.Index, text)))
        {
            return;
        }

        CandidateCount++;
        if (_candidates.Count < ExtractionTrace.MaximumCandidates)
        {
            _candidates.Add(new TracedCandidate(line.Index, line.PageNumber, text, penalty));
        }
    }
}

/// <param name="Label">Rótulo procurado, já normalizado.</param>
/// <param name="LineIndexes">Linhas do texto em que ele apareceu; vazio quando não apareceu.</param>
public sealed record TracedLabel(string Label, IReadOnlyList<int> LineIndexes);

/// <param name="LineIndex">Posição da linha na ordem de leitura.</param>
/// <param name="Page">Página da linha.</param>
/// <param name="Text">Texto candidato a valor. É conteúdo do documento.</param>
/// <param name="Penalty">Quanto a distância ao rótulo descontou da confiança.</param>
public sealed record TracedCandidate(int LineIndex, int Page, string Text, decimal Penalty);
