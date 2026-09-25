namespace DocReader.Application.Extraction;

/// <summary>
/// Retângulo envolvente de uma linha de OCR, em pixels da imagem analisada. As regras espaciais dos
/// extratores (valor à direita ou abaixo do rótulo) trabalham sobre ele, não sobre o polígono.
/// </summary>
public readonly record struct LineBox(decimal Left, decimal Top, decimal Right, decimal Bottom)
{
    public decimal Width => Right - Left;

    public decimal Height => Bottom - Top;

    public decimal CenterY => (Top + Bottom) / 2m;

    /// <summary>Envolve os pares x/y do polígono. Devolve null quando o provedor não mandou coordenadas.</summary>
    public static LineBox? From(IReadOnlyList<decimal> polygon)
    {
        if (polygon.Count < 4)
        {
            return null;
        }

        var left = decimal.MaxValue;
        var top = decimal.MaxValue;
        var right = decimal.MinValue;
        var bottom = decimal.MinValue;

        for (var index = 0; index + 1 < polygon.Count; index += 2)
        {
            left = Math.Min(left, polygon[index]);
            right = Math.Max(right, polygon[index]);
            top = Math.Min(top, polygon[index + 1]);
            bottom = Math.Max(bottom, polygon[index + 1]);
        }

        return new LineBox(left, top, right, bottom);
    }
}
