namespace DocReader.Application.Abstractions;

/// <summary>
/// Counts pages of an accepted upload, so the configured page limit can be enforced before the
/// document is queued.
/// </summary>
public interface IPageCounter
{
    /// <summary>
    /// Counts pages of a seekable stream. Implementations restore the stream position.
    /// </summary>
    /// <exception cref="Errors.UnreadableDocumentException">The file cannot be parsed.</exception>
    Task<int> CountPagesAsync(Stream content, string mimeType, CancellationToken ct);
}
