namespace DocReader.Application.Errors;

/// <summary>The document's retention period ended and its original file was removed on purpose.</summary>
public sealed class DocumentPurgedException(Guid documentId) : Exception($"Document {documentId} was purged.")
{
    public Guid DocumentId { get; } = documentId;
}
