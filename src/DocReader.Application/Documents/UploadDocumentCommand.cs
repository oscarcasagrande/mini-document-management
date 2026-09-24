using DocReader.Domain.Documents;

namespace DocReader.Application.Documents;

/// <summary>
/// One upload request. <paramref name="Content"/> must be seekable: the service reads it more than
/// once to sniff the signature, hash the bytes, count pages and finally store the blob.
/// </summary>
/// <param name="Content">Seekable stream of the received bytes.</param>
/// <param name="OriginalFileName">Name supplied by the client, kept as metadata only.</param>
/// <param name="DeclaredContentType">Content type announced by the client, never trusted.</param>
/// <param name="ExpectedDocumentType">Optional hint about the document type.</param>
/// <param name="ExternalReference">Optional caller reference.</param>
/// <param name="Channel">WEB when it came from the interface, API otherwise.</param>
/// <param name="IdempotencyKey">Optional value of the Idempotency-Key header.</param>
public sealed record UploadDocumentCommand(
    Stream Content,
    string? OriginalFileName,
    string? DeclaredContentType,
    string? ExpectedDocumentType,
    string? ExternalReference,
    UploadChannel Channel,
    string? IdempotencyKey);
