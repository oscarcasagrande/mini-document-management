using System.ComponentModel.DataAnnotations;

namespace DocReader.Application.Options;

/// <summary>
/// Upload limits of RF-002, all configurable.
/// </summary>
public sealed class UploadOptions
{
    public const string SectionName = "DocReader:Upload";

    /// <summary>Maximum accepted size in bytes. Default is 25 MB.</summary>
    [Range(1, 1_073_741_824)]
    public long MaxSizeBytes { get; set; } = 26_214_400;

    /// <summary>Maximum accepted number of pages. Default is 50.</summary>
    [Range(1, 10_000)]
    public int MaxPageCount { get; set; } = 50;

    /// <summary>Maximum length accepted for the optional external reference.</summary>
    [Range(1, 512)]
    public int MaxExternalReferenceLength { get; set; } = 128;

    /// <summary>Maximum length accepted for the optional expected document type.</summary>
    [Range(1, 256)]
    public int MaxExpectedDocumentTypeLength { get; set; } = 64;

    /// <summary>Maximum length kept from the original file name.</summary>
    [Range(1, 512)]
    public int MaxFileNameLength { get; set; } = 255;
}
