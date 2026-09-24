namespace DocReader.Domain.Files;

/// <summary>
/// Outcome of sniffing the real format of an upload from its magic bytes.
/// </summary>
/// <param name="Outcome">Whether the file may be accepted.</param>
/// <param name="MimeType">Detected MIME type, when supported.</param>
/// <param name="Extension">Extension used to build the internal storage key, including the dot.</param>
/// <param name="SignatureName">Short label of the signature, safe to log.</param>
public sealed record FileInspectionResult(
    FileInspectionOutcome Outcome,
    string? MimeType,
    string? Extension,
    string? SignatureName)
{
    public bool IsSupported => Outcome == FileInspectionOutcome.Supported;
}
