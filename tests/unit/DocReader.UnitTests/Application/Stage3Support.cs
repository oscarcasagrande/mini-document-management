using System.Text.Json;
using DocReader.Application.Abstractions;

namespace DocReader.UnitTests.Application;

/// <summary>
/// Constrói resultados de OCR para os testes dos extratores da Etapa 3: sem geometria (ordem de leitura),
/// com geometria (regras espaciais) e a partir das fixtures capturadas do ocr-service de verdade.
/// </summary>
internal static class Stage3Support
{
    /// <summary>Data fixa dos testes: as validações de data dependem de "hoje".</summary>
    public static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Linhas em ordem de leitura, sem coordenadas: só valem as regras de vizinhança por ordem.</summary>
    public static OcrResult Of(params string[] lines) =>
        new(
            "paddleocr",
            "PP-OCRv5",
            [new OcrPage(1, string.Join('\n', lines), [.. lines.Select(line => new OcrBlock(line, 0.95m, []))])],
            "{}");

    /// <summary>Linha com retângulo (x, y, largura, altura), na ordem em que o OCR a devolveria.</summary>
    public readonly record struct Placed(string Text, decimal X, decimal Y, decimal Width = 300m, decimal Height = 24m);

    public static OcrResult Laid(params Placed[] items) =>
        new(
            "paddleocr",
            "PP-OCRv5",
            [
                new OcrPage(
                    1,
                    string.Join('\n', items.Select(item => item.Text)),
                    [.. items.Select(item => new OcrBlock(item.Text, 0.97m, Rectangle(item)))])
            ],
            "{}");

    private static decimal[] Rectangle(Placed item) =>
    [
        item.X, item.Y,
        item.X + item.Width, item.Y,
        item.X + item.Width, item.Y + item.Height,
        item.X, item.Y + item.Height
    ];

    /// <summary>Uma página por lista de linhas, sem coordenadas.</summary>
    public static OcrResult Pages(params string[][] pages) =>
        new(
            "paddleocr",
            "PP-OCRv5",
            [.. pages.Select((lines, index) => new OcrPage(
                index + 1,
                string.Join('\n', lines),
                [.. lines.Select(line => new OcrBlock(line, 0.95m, []))]))],
            "{}");

    /// <summary>
    /// Carrega o que o ocr-service devolveu para uma amostra, capturado por
    /// <c>scripts/capture-ocr-fixtures.py</c>. É o texto real, com a ordem e as coordenadas reais.
    /// </summary>
    public static OcrResult Fixture(string name)
    {
        // DOCREADER_OCR_FIXTURES aponta para outra pasta de fixtures, o que permite rodar a mesma bateria
        // sobre o OCR de outra engine (comparação do ADR 0002) sem tocar nas fixtures versionadas.
        var directory = Environment.GetEnvironmentVariable("DOCREADER_OCR_FIXTURES")
            ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "ocr");
        var path = Path.Combine(directory, $"{name}.ocr.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var pages = new List<OcrPage>();
        foreach (var page in document.RootElement.GetProperty("pages").EnumerateArray())
        {
            var blocks = page.GetProperty("blocks").EnumerateArray()
                .Select(block => new OcrBlock(
                    block.GetProperty("text").GetString()!,
                    block.TryGetProperty("confidence", out var confidence) && confidence.ValueKind == JsonValueKind.Number
                        ? confidence.GetDecimal()
                        : null,
                    [.. block.GetProperty("boundingBox").EnumerateArray().Select(value => value.GetDecimal())]))
                .ToArray();

            pages.Add(new OcrPage(
                page.GetProperty("page").GetInt32(),
                string.Join('\n', blocks.Select(block => block.Text)),
                blocks));
        }

        return new OcrResult("paddleocr", "PP-OCRv5 (fixture)", pages, "{}");
    }

    /// <summary>O <c>expected.json</c> de uma amostra: tipo e, por campo, valor normalizado e status.</summary>
    public static ExpectedSample Expected(string name)
    {
        // As amostras são copiadas para samples/ na saída do teste, sem subpasta (ver o csproj).
        var path = Path.Combine(AppContext.BaseDirectory, "samples", $"{name}.expected.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var fields = new Dictionary<string, (string? Normalized, string Status)>(StringComparer.Ordinal);
        foreach (var field in document.RootElement.GetProperty("fields").EnumerateObject())
        {
            fields[field.Name] = (
                field.Value.GetProperty("normalized").GetString(),
                field.Value.GetProperty("status").GetString()!);
        }

        return new ExpectedSample(document.RootElement.GetProperty("documentType").GetString()!, fields);
    }

    public sealed record ExpectedSample(string DocumentType, IReadOnlyDictionary<string, (string? Normalized, string Status)> Fields);
}
