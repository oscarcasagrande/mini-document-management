using System.ComponentModel.DataAnnotations;

namespace DocReader.Application.Options;

/// <summary>
/// How the worker reaches the internal OCR service (RF-008). The section is shared with the API,
/// which only reads the base address to probe readiness.
/// </summary>
public sealed class OcrProviderOptions
{
    public const string SectionName = "DocReader:Ocr";

    [Required]
    public string BaseUrl { get; set; } = "http://ocr-service:8000";

    /// <summary>
    /// Wall clock budget of one page, measured by the worker. It has to be a little above the
    /// service's own page timeout, so the service answers 504 first and the worker reports a precise error
    /// instead of cutting the connection.
    /// </summary>
    public TimeSpan PageTimeout { get; set; } = TimeSpan.FromSeconds(150);

    /// <summary>Language hints sent along with the file; Portuguese first.</summary>
    public string[] Languages { get; set; } = ["pt"];
}
