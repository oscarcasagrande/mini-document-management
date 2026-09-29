using DocReader.Application.Abstractions;
using DocReader.Application.Classification;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Domain.Catalog;
using Microsoft.Extensions.Logging;

namespace DocReader.Application.Catalog;

/// <summary>
/// Registry of the document types classification and extraction consult at runtime (Configuração Dinâmica).
/// The seven built-in types migrated from code cannot be deleted, only deactivated or have their rules
/// changed; a custom type can be removed once nothing refers to its code.
/// </summary>
public sealed class DocumentTypeService(
    IDocumentTypeRepository repository,
    TimeProvider timeProvider,
    ILogger<DocumentTypeService> logger)
{
    public Task<PagedResult<DocumentType>> ListAsync(DocumentTypeFilter filter, CancellationToken ct) =>
        repository.ListAsync(filter, ct);

    public async Task<DocumentType> GetAsync(Guid id, CancellationToken ct) =>
        await repository.FindByIdAsync(id, ct).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("document-type", id.ToString());

    public async Task<DocumentType> CreateAsync(
        string? code,
        string? name,
        string? schemaJson,
        string? classificationRulesJson,
        string? extractionRulesJson,
        bool active,
        CancellationToken ct)
    {
        var normalized = DocumentType.NormalizeCode(code)
            ?? throw new RequestValidationException(
                "INVALID_DOCUMENT_TYPE_CODE",
                $"The code must have 1 to {DocumentType.MaxCodeLength} letters, digits, dots, hyphens or underscores, starting with a letter or digit.");
        var cleanName = ValidateName(name);
        var schema = ValidateJson(schemaJson, "schema", "INVALID_DOCUMENT_TYPE_SCHEMA");
        var classificationRules = ValidateClassificationRules(normalized, classificationRulesJson);
        var extractionRules = ValidateJson(extractionRulesJson, "extractionRules", "INVALID_DOCUMENT_TYPE_EXTRACTION_RULES");

        if (await repository.FindByCodeAsync(normalized, ct).ConfigureAwait(false) is not null)
        {
            throw new ResourceConflictException(
                "DOCUMENT_TYPE_CODE_EXISTS",
                $"A document type with the code {normalized} already exists.");
        }

        var now = timeProvider.GetUtcNow();
        var documentType = DocumentType.Create(
            Guid.CreateVersion7(now), normalized, cleanName, schema, classificationRules, extractionRules, active, isBuiltIn: false, now);

        await repository.AddAsync(documentType, ct).ConfigureAwait(false);

        logger.LogInformation(
            "Document type created. documentTypeId={DocumentTypeId} code={Code}",
            documentType.Id,
            documentType.Code);

        return documentType;
    }

    public async Task<DocumentType> UpdateAsync(
        Guid id,
        string? name,
        string? schemaJson,
        string? classificationRulesJson,
        string? extractionRulesJson,
        bool active,
        CancellationToken ct)
    {
        var documentType = await GetAsync(id, ct).ConfigureAwait(false);
        var cleanName = ValidateName(name);
        var schema = ValidateJson(schemaJson, "schema", "INVALID_DOCUMENT_TYPE_SCHEMA");
        var classificationRules = ValidateClassificationRules(documentType.Code, classificationRulesJson);
        var extractionRules = ValidateJson(extractionRulesJson, "extractionRules", "INVALID_DOCUMENT_TYPE_EXTRACTION_RULES");

        var now = timeProvider.GetUtcNow();
        documentType.Update(cleanName, schema, classificationRules, extractionRules, active, now);
        await repository.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Document type updated. documentTypeId={DocumentTypeId} code={Code} active={Active}",
            documentType.Id,
            documentType.Code,
            documentType.Active);

        return documentType;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var documentType = await GetAsync(id, ct).ConfigureAwait(false);

        if (documentType.IsBuiltIn)
        {
            throw new ResourceConflictException(
                "DOCUMENT_TYPE_BUILT_IN_PROTECTED",
                $"{documentType.Code} is one of the built-in document types and cannot be deleted. Deactivate it (active = false) or change its rules instead.");
        }

        if (await repository.IsReferencedAsync(documentType.Code, ct).ConfigureAwait(false))
        {
            throw new ResourceConflictException(
                "DOCUMENT_TYPE_IN_USE",
                "Documents or retention policies still refer to this document type. Deactivate it (active = false) instead of deleting it.");
        }

        await repository.RemoveAsync(documentType, ct).ConfigureAwait(false);

        logger.LogInformation("Document type deleted. documentTypeId={DocumentTypeId} code={Code}", id, documentType.Code);
    }

    private static string ValidateName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new RequestValidationException("INVALID_DOCUMENT_TYPE_NAME", "The name is required.");
        }

        return trimmed.Length > DocumentType.MaxNameLength
            ? throw new RequestValidationException(
                "INVALID_DOCUMENT_TYPE_NAME",
                $"The name must not exceed {DocumentType.MaxNameLength} characters.")
            : trimmed;
    }

    private static string ValidateJson(string? json, string field, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new RequestValidationException(errorCode, $"{field} is required and must be well-formed JSON.");
        }

        return DocumentType.IsWellFormedJson(json)
            ? json
            : throw new RequestValidationException(errorCode, $"{field} must be well-formed JSON.");
    }

    /// <summary>
    /// Validates that <paramref name="classificationRulesJson"/> is not just well-formed JSON but a rule the
    /// classifier can actually use: an evidence list and a threshold between 0 and 1.
    /// </summary>
    private static string ValidateClassificationRules(string documentTypeCode, string? classificationRulesJson)
    {
        var json = ValidateJson(classificationRulesJson, "classificationRules", "INVALID_DOCUMENT_TYPE_CLASSIFICATION_RULES");

        return DocumentTypeProfile.TryParse(documentTypeCode, json, out _, out var error)
            ? json
            : throw new RequestValidationException(
                "INVALID_DOCUMENT_TYPE_CLASSIFICATION_RULES",
                $"classificationRules is not usable by the classifier: {error}");
    }
}
