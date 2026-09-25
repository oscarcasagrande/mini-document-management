using System.Globalization;
using System.Text;

namespace DocReader.Domain.Validation;

/// <summary>
/// As 27 unidades federativas, para normalizar e validar a UF lida de um documento (RF-012).
/// </summary>
public static class BrazilianState
{
    private static readonly Dictionary<string, string> NamesByCode = new(StringComparer.Ordinal)
    {
        ["AC"] = "ACRE",
        ["AL"] = "ALAGOAS",
        ["AP"] = "AMAPA",
        ["AM"] = "AMAZONAS",
        ["BA"] = "BAHIA",
        ["CE"] = "CEARA",
        ["DF"] = "DISTRITO FEDERAL",
        ["ES"] = "ESPIRITO SANTO",
        ["GO"] = "GOIAS",
        ["MA"] = "MARANHAO",
        ["MT"] = "MATO GROSSO",
        ["MS"] = "MATO GROSSO DO SUL",
        ["MG"] = "MINAS GERAIS",
        ["PA"] = "PARA",
        ["PB"] = "PARAIBA",
        ["PR"] = "PARANA",
        ["PE"] = "PERNAMBUCO",
        ["PI"] = "PIAUI",
        ["RJ"] = "RIO DE JANEIRO",
        ["RN"] = "RIO GRANDE DO NORTE",
        ["RS"] = "RIO GRANDE DO SUL",
        ["RO"] = "RONDONIA",
        ["RR"] = "RORAIMA",
        ["SC"] = "SANTA CATARINA",
        ["SP"] = "SAO PAULO",
        ["SE"] = "SERGIPE",
        ["TO"] = "TOCANTINS"
    };

    private static readonly Dictionary<string, string> CodesByName =
        NamesByCode.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    public static bool IsValid(string? code) =>
        code is { Length: 2 } && NamesByCode.ContainsKey(code.ToUpperInvariant());

    /// <summary>
    /// Aceita a sigla (<c>sp</c>, <c>SP</c>) ou o nome por extenso (<c>São Paulo</c>) e devolve a sigla
    /// em caixa alta. Devolve false para qualquer outra coisa.
    /// </summary>
    public static bool TryNormalize(string? value, out string code)
    {
        code = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = Flatten(value);

        if (candidate.Length == 2 && NamesByCode.ContainsKey(candidate))
        {
            code = candidate;
            return true;
        }

        return CodesByName.TryGetValue(candidate, out code!);
    }

    private static string Flatten(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = true;

        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(char.ToUpperInvariant(character));
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }
}
