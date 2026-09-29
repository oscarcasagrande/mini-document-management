namespace DocReader.Domain.StorageMigrations;

/// <summary>
/// Narrows the documents a <see cref="StorageMigrationJob"/> moves. Every criterion is optional and they combine
/// with AND; with none, every document on the source repository is moved.
/// </summary>
/// <param name="DocumentType">Detected document type (BR_CNH, ...), or null for any.</param>
/// <param name="ProductServiceId">Product or service the document was uploaded for, or null for any.</param>
/// <param name="UploadedFrom">Inclusive lower bound of the upload instant, in UTC, or null.</param>
/// <param name="UploadedTo">Inclusive upper bound of the upload instant, in UTC, or null.</param>
public sealed record StorageMigrationFilter(
    string? DocumentType,
    Guid? ProductServiceId,
    DateTimeOffset? UploadedFrom,
    DateTimeOffset? UploadedTo)
{
    public static StorageMigrationFilter None { get; } = new(null, null, null, null);

    public bool IsEmpty => DocumentType is null && ProductServiceId is null && UploadedFrom is null && UploadedTo is null;
}
