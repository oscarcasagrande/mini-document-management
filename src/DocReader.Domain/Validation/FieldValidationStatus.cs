namespace DocReader.Domain.Validation;

/// <summary>
/// Estado de um campo extraído, conforme RF-012 do PRD.
/// </summary>
public enum FieldValidationStatus
{
    /// <summary>Encontrado e aprovado pelas regras determinísticas do tipo.</summary>
    Valid = 0,

    /// <summary>Encontrado, porém reprovado: dígito verificador errado, data impossível e afins.</summary>
    Invalid = 1,

    /// <summary>Não localizado no documento. O valor normalizado é sempre null.</summary>
    NotFound = 2,

    /// <summary>Localizado, mas sem evidência suficiente para afirmar o valor.</summary>
    Uncertain = 3
}
