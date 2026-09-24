using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace DocReader.Api.Contracts.V1;

/// <summary>
/// Filters and paging of the listing endpoint. Every field is optional; without filters the whole
/// history is returned, newest first. The query string is camelCase, like every other payload of the
/// API, so the binding names are declared explicitly.
/// </summary>
public sealed class DocumentListRequest
{
    /// <summary>Full or partial protocol, case insensitive.</summary>
    [FromQuery(Name = "protocol")]
    [MaxLength(32)]
    public string? Protocol { get; init; }

    /// <summary>Full or partial original file name, case insensitive.</summary>
    [FromQuery(Name = "fileName")]
    [MaxLength(512)]
    public string? FileName { get; init; }

    /// <summary>Detected or expected document type, matched exactly.</summary>
    [FromQuery(Name = "documentType")]
    [MaxLength(64)]
    public string? DocumentType { get; init; }

    /// <summary>Upload channel: WEB or API.</summary>
    [FromQuery(Name = "channel")]
    [MaxLength(16)]
    public string? Channel { get; init; }

    /// <summary>
    /// Status: RECEIVED, STORED, QUEUED, PREPROCESSING, OCR_RUNNING, CLASSIFYING, EXTRACTING,
    /// COMPLETED, FAILED or REJECTED.
    /// </summary>
    [FromQuery(Name = "status")]
    [MaxLength(32)]
    public string? Status { get; init; }

    /// <summary>Lower bound of the upload instant, inclusive, ISO 8601 in UTC.</summary>
    [FromQuery(Name = "uploadedFrom")]
    public DateTimeOffset? UploadedFrom { get; init; }

    /// <summary>Upper bound of the upload instant, inclusive, ISO 8601 in UTC.</summary>
    [FromQuery(Name = "uploadedTo")]
    public DateTimeOffset? UploadedTo { get; init; }

    /// <summary>One based page number. Defaults to 1.</summary>
    [FromQuery(Name = "page")]
    [Range(1, int.MaxValue)]
    [DefaultValue(1)]
    public int Page { get; init; } = 1;

    /// <summary>Items per page. Defaults to the configured page size and is capped by the maximum.</summary>
    [FromQuery(Name = "pageSize")]
    [Range(1, 500)]
    public int? PageSize { get; init; }
}
