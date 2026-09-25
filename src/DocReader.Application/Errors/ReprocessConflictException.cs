namespace DocReader.Application.Errors;

/// <summary>
/// The document is already queued or being processed, and RF-013 forbids concurrent processing of
/// the same document. Maps to 409.
/// </summary>
public sealed class ReprocessConflictException(Guid documentId)
    : Exception($"Document {documentId} is already being processed.")
{
    public Guid DocumentId { get; } = documentId;
}
