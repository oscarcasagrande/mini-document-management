using DocReader.Domain.Documents;

namespace DocReader.Api.Contracts.V1;

/// <summary>
/// Body of the 202 answered by the upload endpoint. The document is persisted and queued; the OCR
/// runs afterwards, outside the request.
/// </summary>
/// <param name="Id">Identity of the document.</param>
/// <param name="Protocol">Human readable protocol, format DOC-yyyyMMdd-NNNNNN.</param>
/// <param name="Status">Status at the moment of the response.</param>
/// <param name="StatusUrl">Lightweight endpoint for polling.</param>
/// <param name="DocumentUrl">Consolidated detail endpoint.</param>
/// <param name="ContentUrl">Endpoint that serves the original file.</param>
public sealed record UploadAcceptedResponse(
    Guid Id,
    string Protocol,
    DocumentStatus Status,
    string StatusUrl,
    string DocumentUrl,
    string ContentUrl);
