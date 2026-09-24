namespace DocReader.Api.Errors;

/// <summary>
/// Stable problem type URIs. They are documentation anchors, not endpoints to fetch.
/// </summary>
public static class ProblemTypes
{
    public const string ContentType = "application/problem+json";

    private const string BaseUri = "https://docreader.local/problems/";

    public static string Build(string slug) => BaseUri + slug;

    public const string Validation = "validation-failed";
    public const string NotFound = "document-not-found";
    public const string Conflict = "conflict";
    public const string PayloadTooLarge = "file-too-large";
    public const string UnsupportedMediaType = "unsupported-media-type";
    public const string UnprocessableContent = "unprocessable-content";
    public const string ContentUnavailable = "document-content-unavailable";
    public const string Unexpected = "unexpected-error";
}
