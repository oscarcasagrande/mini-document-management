using System.ComponentModel.DataAnnotations;

namespace DocReader.Application.Options;

/// <summary>
/// Local storage settings. The root is a Docker volume in the PoC.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "DocReader:Storage";

    [Required]
    public string RootPath { get; set; } = "/var/lib/docreader/storage";
}
