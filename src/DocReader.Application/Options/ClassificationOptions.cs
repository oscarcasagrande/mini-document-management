using System.ComponentModel.DataAnnotations;

namespace DocReader.Application.Options;

/// <summary>Tuning of the rules classifier. API and worker must be configured alike.</summary>
public sealed class ClassificationOptions
{
    public const string SectionName = "DocReader:Classification";

    /// <summary>
    /// Minimum score for any type, replacing the threshold of each profile. 0 keeps the profile
    /// thresholds. Lower it to accept weaker evidence (more recall, more risk of a wrong type); raise it
    /// to accept only strong evidence.
    /// </summary>
    [Range(0, 1)]
    public decimal MinimumScore { get; set; }
}
