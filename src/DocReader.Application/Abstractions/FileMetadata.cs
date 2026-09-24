namespace DocReader.Application.Abstractions;

/// <summary>
/// Everything the storage needs to persist a blob. The storage key is derived from
/// <paramref name="DocumentId"/> and never from a client supplied name.
/// </summary>
/// <param name="DocumentId">Identity that owns the blob.</param>
/// <param name="Extension">Extension taken from the detected MIME type, including the dot.</param>
/// <param name="MimeType">Detected MIME type.</param>
/// <param name="CreatedAt">Timestamp used to lay out the storage folders.</param>
public sealed record FileMetadata(Guid DocumentId, string Extension, string MimeType, DateTimeOffset CreatedAt);
