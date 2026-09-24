using DocReader.Domain.Documents;

namespace DocReader.Application.Errors;

/// <summary>
/// The document exists but has no result to return: it is still being processed, or it failed. Maps to 409.
/// </summary>
public sealed class ResultNotReadyException(Guid documentId, DocumentStatus status)
    : Exception($"Document {documentId} has no result while it is {status}.")
{
    public Guid DocumentId { get; } = documentId;

    public DocumentStatus Status { get; } = status;
}
