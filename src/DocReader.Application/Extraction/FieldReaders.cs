using System.Text.RegularExpressions;
using DocReader.Application.Abstractions;
using DocReader.Domain.Validation;

namespace DocReader.Application.Extraction;

/// <summary>Que papel a data tem no documento, o que define quais valores são plausíveis.</summary>
internal enum DateKind
{
    /// <summary>Existe, não é futura e não é anterior a 1900.</summary>
    Birth,

    /// <summary>Emissão ou expedição: existe, não é futura e não é anterior a 1900.</summary>
    Issue,

    /// <summary>Validade: pode ser futura, até meio século adiante.</summary>
    Expiration,

    /// <summary>Qualquer data de calendário entre 1900 e meio século adiante (abertura, vencimento).</summary>
    Any
}

/// <summary>
/// Leitores de campo compartilhados pelos extratores: cada um procura o valor a partir dos rótulos,
/// normaliza, valida e devolve o campo com a evidência. Campo que não aparece vira NOT_FOUND, nunca
/// um valor inventado (RF-011).
/// </summary>
internal static class FieldReaders
{
    public const string CheckDigitValid = "CHECK_DIGIT_VALID";
    public const string CheckDigitInvalid = "CHECK_DIGIT_INVALID";
    public const string DateValid = "DATE_VALID";
    public const string DateInvalid = "DATE_INVALID";

    /// <summary>
    /// A data de validade já passou. Não reprova o campo: a data foi lida e é uma data real. Quem consome o
    /// resultado decide o que fazer com um documento vencido.
    /// </summary>
    public const string DocumentExpired = "DOCUMENT_EXPIRED";
    public const string FormatValid = "FORMAT_VALID";
    public const string PostalCodeValid = "POSTAL_CODE_VALID";
    public const string StateValid = "STATE_VALID";

    private const decimal Fallback = FieldFactory.FallbackConfidencePenalty;

    public static ExtractedFieldValue Date(
        LineSearch search,
        IReadOnlyList<string> labels,
        DateKind kind,
        DateOnly today,
        Func<OcrTextLine, bool>? skipLine = null)
    {
        LabelledValue? invalid = null;
        string? invalidRaw = null;

        foreach (var candidate in search.After(labels))
        {
            if (skipLine?.Invoke(candidate.Line) == true)
            {
                continue;
            }

            foreach (Match match in ExtractionPatterns.Date().Matches(candidate.Text))
            {
                if (BrazilianDate.TryParse(match.Value, out var parsed))
                {
                    if (IsPlausible(parsed, kind, today))
                    {
                        var expired = kind == DateKind.Expiration && parsed < today;

                        return FieldFactory.Found(
                            match.Value, BrazilianDate.ToIso(parsed), candidate.Line, candidate.Penalty,
                            FieldValidationStatus.Valid, expired ? new[] { DateValid, DocumentExpired } : new[] { DateValid });
                    }
                }

                invalid ??= candidate;
                invalidRaw ??= match.Value;
            }
        }

        return invalid is null
            ? FieldFactory.NotFound()
            : FieldFactory.Build(
                invalidRaw!, null, invalid.Line, invalid.Penalty + Fallback, FieldValidationStatus.Invalid, [DateInvalid]);
    }

    public static ExtractedFieldValue Name(LineSearch search, IReadOnlyList<string> labels)
    {
        foreach (var candidate in search.After(labels))
        {
            var cleaned = TextNormalization.CleanPersonName(candidate.Text);
            if (cleaned is not null)
            {
                return FieldFactory.Found(candidate.Text.Trim(), cleaned, candidate.Line, candidate.Penalty, FieldValidationStatus.Valid);
            }
        }

        return FieldFactory.NotFound();
    }

    /// <summary>Texto livre em caixa alta e sem acento, para rótulos como cidade, bairro e logradouro.</summary>
    public static ExtractedFieldValue Text(LineSearch search, IReadOnlyList<string> labels, int minimumLength = 2)
    {
        foreach (var candidate in search.After(labels))
        {
            var cleaned = TextNormalization.ForMatching(candidate.Text).Trim(' ', '-', ':', '.', ',');

            // "(NOME DE FANTASIA)" sozinho é o resto de um rótulo que o OCR partiu, não um valor.
            if (cleaned.StartsWith('(') && cleaned.EndsWith(')'))
            {
                continue;
            }

            if (cleaned.Length >= minimumLength && cleaned.Any(char.IsAsciiLetterOrDigit))
            {
                return FieldFactory.Found(candidate.Text.Trim(), cleaned, candidate.Line, candidate.Penalty, FieldValidationStatus.Valid);
            }
        }

        return FieldFactory.NotFound();
    }

    /// <summary>
    /// CPF em quatro preferências: válido perto do rótulo, válido em qualquer lugar (confiança menor),
    /// reprovado perto do rótulo, reprovado em qualquer lugar. Reportar INVALID é mais útil do que
    /// NOT_FOUND: diz que leu e reprovou.
    /// </summary>
    public static ExtractedFieldValue Cpf(LineSearch search, IReadOnlyList<string> labels)
    {
        foreach (var candidate in search.After(labels))
        {
            foreach (Match match in ExtractionPatterns.Cpf().Matches(candidate.Text))
            {
                if (Domain.Validation.Cpf.TryNormalize(match.Value, out var normalized) && Domain.Validation.Cpf.IsValid(normalized))
                {
                    return FieldFactory.Found(match.Value, normalized, candidate.Line, candidate.Penalty, FieldValidationStatus.Valid, CheckDigitValid);
                }
            }
        }

        foreach (var line in search.Lines)
        {
            foreach (Match match in ExtractionPatterns.Cpf().Matches(line.Text))
            {
                if (Domain.Validation.Cpf.TryNormalize(match.Value, out var normalized) && Domain.Validation.Cpf.IsValid(normalized))
                {
                    return FieldFactory.Found(match.Value, normalized, line, Fallback, FieldValidationStatus.Valid, CheckDigitValid);
                }
            }
        }

        foreach (var candidate in search.After(labels))
        {
            var match = ExtractionPatterns.Cpf().Match(candidate.Text);
            if (match.Success && Domain.Validation.Cpf.TryNormalize(match.Value, out _))
            {
                return FieldFactory.Build(match.Value, null, candidate.Line, Fallback + candidate.Penalty, FieldValidationStatus.Invalid, [CheckDigitInvalid]);
            }
        }

        foreach (var line in search.Lines)
        {
            var match = ExtractionPatterns.Cpf().Match(line.Text);
            if (match.Success && Domain.Validation.Cpf.TryNormalize(match.Value, out _))
            {
                return FieldFactory.Found(match.Value, null, line, Fallback, FieldValidationStatus.Invalid, CheckDigitInvalid);
            }
        }

        return FieldFactory.NotFound();
    }

    /// <summary>CNPJ, inclusive o alfanumérico, com as mesmas quatro preferências do CPF.</summary>
    public static ExtractedFieldValue Cnpj(LineSearch search, IReadOnlyList<string> labels)
    {
        foreach (var candidate in search.After(labels))
        {
            foreach (Match match in ExtractionPatterns.Cnpj().Matches(candidate.Text))
            {
                if (Domain.Validation.Cnpj.TryNormalize(match.Value, out var normalized) && Domain.Validation.Cnpj.IsValid(normalized))
                {
                    return FieldFactory.Found(match.Value, normalized, candidate.Line, candidate.Penalty, FieldValidationStatus.Valid, CheckDigitValid);
                }
            }
        }

        foreach (var line in search.Lines)
        {
            foreach (Match match in ExtractionPatterns.Cnpj().Matches(line.Text))
            {
                if (Domain.Validation.Cnpj.TryNormalize(match.Value, out var normalized) && Domain.Validation.Cnpj.IsValid(normalized))
                {
                    return FieldFactory.Found(match.Value, normalized, line, Fallback, FieldValidationStatus.Valid, CheckDigitValid);
                }
            }
        }

        foreach (var candidate in search.After(labels))
        {
            var match = ExtractionPatterns.Cnpj().Match(candidate.Text);
            if (match.Success && Domain.Validation.Cnpj.TryNormalize(match.Value, out _))
            {
                return FieldFactory.Build(match.Value, null, candidate.Line, Fallback + candidate.Penalty, FieldValidationStatus.Invalid, [CheckDigitInvalid]);
            }
        }

        return FieldFactory.NotFound();
    }

    /// <summary>Um CPF ou CNPJ, o que aparecer: comprovantes de residência e certificados trazem qualquer um dos dois.</summary>
    public static ExtractedFieldValue CpfOrCnpj(LineSearch search, IReadOnlyList<string> labels)
    {
        var cnpj = Cnpj(search, labels);
        var cpf = Cpf(search, labels);

        return (cnpj.ValidationStatus, cpf.ValidationStatus) switch
        {
            ("VALID", _) => cnpj,
            (_, "VALID") => cpf,
            ("INVALID", _) => cnpj,
            (_, "INVALID") => cpf,
            _ => FieldFactory.NotFound()
        };
    }

    public static ExtractedFieldValue PostalCode(LineSearch search, IReadOnlyList<string> labels)
    {
        foreach (var candidate in search.After(labels))
        {
            var match = ExtractionPatterns.PostalCode().Match(candidate.Text);
            if (match.Success && Domain.Validation.PostalCode.TryNormalize(match.Value, out var normalized))
            {
                return FieldFactory.Found(match.Value, normalized, candidate.Line, candidate.Penalty, FieldValidationStatus.Valid, PostalCodeValid);
            }
        }

        return FieldFactory.NotFound();
    }

    public static ExtractedFieldValue State(LineSearch search, IReadOnlyList<string> labels)
    {
        foreach (var candidate in search.After(labels))
        {
            var text = candidate.Text.Trim().Trim('-', '/', ':', '.');
            if (BrazilianState.TryNormalize(text, out var code))
            {
                return FieldFactory.Found(candidate.Text.Trim(), code, candidate.Line, candidate.Penalty, FieldValidationStatus.Valid, StateValid);
            }
        }

        return FieldFactory.NotFound();
    }

    public static ExtractedFieldValue Money(LineSearch search, IReadOnlyList<string> labels)
    {
        foreach (var candidate in search.After(labels))
        {
            var match = ExtractionPatterns.Money().Match(candidate.Text);
            if (match.Success && BrazilianMoney.TryParse(match.Value, out var amount))
            {
                return FieldFactory.Found(match.Value, BrazilianMoney.ToInvariant(amount), candidate.Line, candidate.Penalty, FieldValidationStatus.Valid, FormatValid);
            }
        }

        return FieldFactory.NotFound();
    }

    /// <summary>
    /// Atividade econômica, "62.01-5-01 - Desenvolvimento de programas de computador sob encomenda":
    /// o código da CNAE sai normalizado só com dígitos e a descrição em caixa alta e sem acento. Sem
    /// código, só a descrição é devolvida.
    /// </summary>
    public static (ExtractedFieldValue Code, ExtractedFieldValue Description) Activity(
        LineSearch search,
        IReadOnlyList<string> labels)
    {
        foreach (var candidate in search.After(labels))
        {
            var match = ExtractionPatterns.Cnae().Match(candidate.Text);
            var remainder = match.Success
                ? (candidate.Text[..match.Index] + " " + candidate.Text[(match.Index + match.Length)..])
                : candidate.Text;

            var description = TextNormalization.ForMatching(remainder).Trim(' ', '-', ':', '.', ',', '/');
            var hasDescription = description.Length >= 3 && description.Any(char.IsAsciiLetter);

            var code = match.Success
                ? FieldFactory.Found(
                    match.Value,
                    new string([.. match.Value.Where(char.IsAsciiDigit)]),
                    candidate.Line,
                    candidate.Penalty,
                    FieldValidationStatus.Valid,
                    FormatValid)
                : FieldFactory.NotFound();

            var text = hasDescription
                ? FieldFactory.Found(remainder.Trim(' ', '-', ':'), description, candidate.Line, candidate.Penalty, FieldValidationStatus.Valid)
                : FieldFactory.NotFound();

            if (match.Success || hasDescription)
            {
                return (code, text);
            }
        }

        return (FieldFactory.NotFound(), FieldFactory.NotFound());
    }

    private static bool IsPlausible(DateOnly date, DateKind kind, DateOnly today) => kind switch
    {
        DateKind.Birth or DateKind.Issue => BrazilianDate.IsPlausibleBirthDate(date, today),
        _ => date >= BrazilianDate.MinimumPlausible && date <= today.AddYears(50)
    };
}
