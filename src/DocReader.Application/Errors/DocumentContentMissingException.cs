namespace DocReader.Application.Errors;

/// <summary>
/// Metadata exists but the stored blob is gone, which means the storage volume was wiped without
/// the database.
/// </summary>
public sealed class DocumentContentMissingException(Guid documentId)
    : Exception($"Stored content for document {documentId} is not available.")
{
    public Guid DocumentId { get; } = documentId;
}
