namespace DocReader.Api.Options;

/// <summary>
/// Where the internal OCR service lives. The API only probes its health, as required by the readiness
/// contract of RF-016; the worker is the one that sends pages to it.
/// </summary>
public sealed class OcrServiceOptions
{
    public const string SectionName = "DocReader:Ocr";

    public string BaseUrl { get; set; } = "http://ocr-service:8000";

    public string HealthPath { get; set; } = "/health";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);
}
