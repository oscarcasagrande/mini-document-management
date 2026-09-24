using DocReader.Domain.Documents;

namespace DocReader.Application.Documents;

/// <summary>
/// Outcome of an accepted upload.
/// </summary>
/// <param name="DocumentId">Identity of the document.</param>
/// <param name="Protocol">Human readable protocol.</param>
/// <param name="Status">Status at the moment of the response.</param>
/// <param name="Replayed">
/// True when an Idempotency-Key replay returned the original response instead of creating a
/// second document.
/// </param>
public sealed record UploadDocumentResult(Guid DocumentId, string Protocol, DocumentStatus Status, bool Replayed);
