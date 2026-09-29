using DocReader.Application.Abstractions;
using DocReader.Application.Catalog;
using DocReader.Application.Classification;
using DocReader.Application.Documents;
using DocReader.Application.Errors;
using DocReader.Domain.Catalog;

namespace DocReader.UnitTests.Fakes;

/// <summary>
/// In-memory <see cref="IDocumentTypeRepository"/> for the use case tests. Seeded by default with the seven
/// built-in types, generated from <see cref="DocumentTypeProfile.All"/>, so tests that only care about
/// "the document types the classifier knows about" (retention policies, for instance) behave exactly as
/// they did when that list was hardcoded.
/// </summary>
public sealed class InMemoryDocumentTypeStore : IDocumentTypeRepository
{
    private static readonly DateTimeOffset SeedBasis = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public List<DocumentType> Items { get; } = [];

    /// <summary>Stands in for the documents and retention policies that point at a type's code.</summary>
    public Func<string, bool> IsReferenced { get; set; } = _ => false;

    public static InMemoryDocumentTypeStore WithBuiltIns()
    {
        var store = new InMemoryDocumentTypeStore();

        for (var index = 0; index < DocumentTypeProfile.All.Count; index++)
        {
            var profile = DocumentTypeProfile.All[index];
            var now = SeedBasis.AddSeconds(index);

            store.Items.Add(DocumentType.Create(
                Guid.CreateVersion7(now),
                profile.DocumentType,
                profile.DocumentType,
                "{}",
                profile.ToClassificationRulesJson(),
                "{}",
                active: true,
                isBuiltIn: true,
                now));
        }

        return store;
    }

    public Task<DocumentType?> FindByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(item => item.Id == id));

    public Task<DocumentType?> FindByCodeAsync(string code, CancellationToken ct) =>
        Task.FromResult(Items.FirstOrDefault(item => item.Code == code));

    public Task<PagedResult<DocumentType>> ListAsync(DocumentTypeFilter filter, CancellationToken ct)
    {
        var query = Items.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter.Code))
        {
            query = query.Where(item => item.Code.Contains(filter.Code.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.Name))
        {
            query = query.Where(item => item.Name.Contains(filter.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (filter.Active is { } active)
        {
            query = query.Where(item => item.Active == active);
        }

        var ordered = query.OrderBy(item => item.Code, StringComparer.Ordinal).ToList();
        var page = ordered.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToList();

        return Task.FromResult(new PagedResult<DocumentType>(page, filter.Page, filter.PageSize, ordered.Count));
    }

    public Task<IReadOnlyList<DocumentType>> ListActiveAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DocumentType>>(
            [.. Items.Where(item => item.Active).OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)]);

    public Task AddAsync(DocumentType documentType, CancellationToken ct)
    {
        if (Items.Any(item => item.Code == documentType.Code))
        {
            throw new ResourceConflictException("DOCUMENT_TYPE_CODE_EXISTS", "duplicate");
        }

        Items.Add(documentType);

        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<bool> IsReferencedAsync(string code, CancellationToken ct) => Task.FromResult(IsReferenced(code));

    public Task RemoveAsync(DocumentType documentType, CancellationToken ct)
    {
        Items.Remove(documentType);

        return Task.CompletedTask;
    }
}
