using System.Text.Json;
using DocReader.Application.Abstractions;

namespace DocReader.Application.Extraction;

/// <summary>
/// Refaz o <see cref="OcrResult"/> que o extrator recebeu, a partir do que ficou gravado. Extrações novas guardam
/// os blocos normalizados com coordenadas junto do payload do provedor; as anteriores só têm o texto das páginas,
/// então a reconstrução cai para linhas sem geometria e o diagnóstico avisa.
/// </summary>
internal static class StoredOcr
{
    public const string Blocks = "BLOCKS";
    public const string PageText = "PAGE_TEXT";

    public static (OcrResult Result, string Source) Rebuild(string rawOcrResultJson, string pageTextsJson, string provider, string modelVersion)
    {
        var pages = FromBlocks(rawOcrResultJson);
        if (pages is not null)
        {
            return (new OcrResult(provider, modelVersion, pages, "{}"), Blocks);
        }

        return (new OcrResult(provider, modelVersion, FromPageTexts(pageTextsJson), "{}"), PageText);
    }

    private static List<OcrPage>? FromBlocks(string rawOcrResultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawOcrResultJson);
            if (!document.RootElement.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new List<OcrPage>();
            foreach (var page in pages.EnumerateArray())
            {
                if (!page.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var parsed = blocks.EnumerateArray()
                    .Select(block => new OcrBlock(
                        block.GetProperty("text").GetString() ?? string.Empty,
                        block.TryGetProperty("confidence", out var confidence) && confidence.ValueKind == JsonValueKind.Number
                            ? confidence.GetDecimal()
                            : null,
                        [.. block.GetProperty("boundingBox").EnumerateArray().Select(value => value.GetDecimal())]))
                    .ToArray();

                result.Add(new OcrPage(
                    page.GetProperty("page").GetInt32(),
                    string.Join('\n', parsed.Select(block => block.Text)),
                    parsed));
            }

            return result.Count == 0 ? null : result;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static List<OcrPage> FromPageTexts(string pageTextsJson)
    {
        var stored = JsonSerializer.Deserialize<List<StoredPage>>(pageTextsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];

        return
        [
            .. stored.Select(page => new OcrPage(
                page.PageNumber,
                page.Text,
                [.. page.Text.Split('\n').Select(line => new OcrBlock(line, null, []))]))
        ];
    }

    private sealed record StoredPage(int PageNumber, string Text);
}
