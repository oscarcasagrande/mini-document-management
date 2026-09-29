using DocReader.Application.Catalog;
using DocReader.Application.Documents;
using DocReader.Domain.Catalog;

namespace DocReader.Application.Abstractions;

/// <summary>Persistence of the document types. Finds return tracked entities, so a change is saved with <see cref="SaveChangesAsync"/>.</summary>
public interface IDocumentTypeRepository
{
    Task<DocumentType?> FindByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Looks a type up by its canonical (upper case) code.</summary>
    Task<DocumentType?> FindByCodeAsync(string code, CancellationToken ct);

    Task<PagedResult<DocumentType>> ListAsync(DocumentTypeFilter filter, CancellationToken ct);

    /// <summary>Every active type, oldest first (the built-ins keep the order they were seeded in), for classification.</summary>
    Task<IReadOnlyList<DocumentType>> ListActiveAsync(CancellationToken ct);

    /// <exception cref="Errors.ResourceConflictException">The code is already taken.</exception>
    Task AddAsync(DocumentType documentType, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>Whether a document (detected or expected type) or a retention policy still points at this type's code.</summary>
    Task<bool> IsReferencedAsync(string code, CancellationToken ct);

    Task RemoveAsync(DocumentType documentType, CancellationToken ct);
}
