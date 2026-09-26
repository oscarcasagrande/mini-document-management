using DocReader.Domain.Documents;

namespace DocReader.Application.Documents;

/// <summary>
/// Filters and paging of the listing endpoint (RF-005). Values are already validated by the API.
/// </summary>
/// <param name="Protocol">Exact or partial protocol.</param>
/// <param name="FileName">Partial original file name, case insensitive.</param>
/// <param name="DocumentType">Detected or expected type.</param>
/// <param name="Channel">WEB or API.</param>
/// <param name="Status">Current status.</param>
/// <param name="UploadedFrom">Lower bound of the upload timestamp, inclusive.</param>
/// <param name="UploadedTo">Upper bound of the upload timestamp, inclusive.</param>
/// <param name="Page">One based page number.</param>
/// <param name="PageSize">Number of items per page.</param>
/// <param name="ProductServiceCode">Code of the product or service the document is linked to.</param>
/// <param name="ExternalReference">Full or partial caller reference, case insensitive.</param>
public sealed record DocumentListFilter(
    string? Protocol,
    string? FileName,
    string? DocumentType,
    UploadChannel? Channel,
    DocumentStatus? Status,
    DateTimeOffset? UploadedFrom,
    DateTimeOffset? UploadedTo,
    int Page,
    int PageSize,
    string? ProductServiceCode = null,
    string? ExternalReference = null);
