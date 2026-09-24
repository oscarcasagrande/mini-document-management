namespace DocReader.Api.Options;

/// <summary>
/// Where the internal OCR service lives. In stage 1 the API only probes its health, as required by
/// the readiness contract of RF-016.
/// </summary>
public sealed class OcrServiceOptions
{
    public const string SectionName = "DocReader:Ocr";

    public string BaseUrl { get; set; } = "http://ocr-service:8000";

    public string HealthPath { get; set; } = "/health";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);
}
