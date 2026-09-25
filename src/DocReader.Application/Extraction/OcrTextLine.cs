using DocReader.Application.Abstractions;

namespace DocReader.Application.Extraction;

/// <summary>
/// Uma linha de texto lida pelo OCR, achatada em ordem de leitura.
///
/// Os extratores trabalham sobre esta forma e não sobre o payload cru do provedor: tanto o PP-OCRv5
/// quanto o PP-StructureV3 produzem linhas com caixa e confiança, então a escolha de engine não
/// vaza para a regra de extração.
/// </summary>
/// <param name="Index">Posição na ordem de leitura, a partir de 0.</param>
/// <param name="PageNumber">Página de origem, a partir de 1.</param>
/// <param name="Text">Texto da linha, como saiu do OCR.</param>
/// <param name="Confidence">Confiança do provedor entre 0 e 1, quando disponível.</param>
/// <param name="BoundingBox">Polígono da linha como pares x/y, vazio quando indisponível.</param>
public sealed record OcrTextLine(
    int Index,
    int PageNumber,
    string Text,
    decimal? Confidence,
    IReadOnlyList<decimal> BoundingBox)
{
    /// <summary>Texto sem acento, em caixa alta e com espaços colapsados, para casar rótulos.</summary>
    public string Normalized { get; } = TextNormalization.ForMatching(Text);

    /// <summary>Retângulo da linha, ou null quando o provedor não mandou coordenadas.</summary>
    public LineBox? Box { get; } = LineBox.From(BoundingBox);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    /// <summary>Achata o resultado do OCR em linhas, preservando a ordem de leitura.</summary>
    public static IReadOnlyList<OcrTextLine> From(OcrResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lines = new List<OcrTextLine>();

        foreach (var page in result.Pages)
        {
            foreach (var block in page.Blocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text))
                {
                    continue;
                }

                lines.Add(new OcrTextLine(
                    lines.Count,
                    page.PageNumber,
                    block.Text.Trim(),
                    block.Confidence,
                    block.BoundingBox));
            }
        }

        return lines;
    }
}
