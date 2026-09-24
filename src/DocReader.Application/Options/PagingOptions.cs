using System.ComponentModel.DataAnnotations;

namespace DocReader.Application.Options;

/// <summary>
/// Paging limits of the listing endpoint.
/// </summary>
public sealed class PagingOptions
{
    public const string SectionName = "DocReader:Paging";

    [Range(1, 200)]
    public int DefaultPageSize { get; set; } = 20;

    [Range(1, 500)]
    public int MaxPageSize { get; set; } = 100;
}
