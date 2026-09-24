using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace DocReader.Api.Contracts.V1;

/// <summary>
/// Multipart body of the upload endpoint.
/// </summary>
public sealed class UploadDocumentRequest
{
    /// <summary>
    /// The document itself. PDF, PNG, JPEG or TIFF, validated by its real signature and not by the
    /// announced content type.
    /// </summary>
    [Required]
    public IFormFile? File { get; init; }

    /// <summary>
    /// Optional hint about the document type. It never overrides the classifier; a divergence is
    /// recorded instead.
    /// </summary>
    [MaxLength(64)]
    public string? ExpectedDocumentType { get; init; }

    /// <summary>Optional reference of the calling system, echoed back in every response.</summary>
    [MaxLength(128)]
    public string? ExternalReference { get; init; }
}
