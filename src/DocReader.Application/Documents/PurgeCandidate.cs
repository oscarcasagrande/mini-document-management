namespace DocReader.Application.Documents;

/// <summary>What the purge job needs to remove a document's file: never its content or its file name.</summary>
/// <param name="DocumentId">The document.</param>
/// <param name="Protocol">Its protocol, for the log.</param>
/// <param name="StorageRepositoryId">The repository the file is in.</param>
/// <param name="StorageKey">The key of the file in that repository.</param>
public sealed record PurgeCandidate(Guid DocumentId, string Protocol, Guid StorageRepositoryId, string StorageKey);
