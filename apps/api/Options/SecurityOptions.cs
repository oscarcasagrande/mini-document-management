namespace DocReader.Api.Options;

/// <summary>
/// Minimum security knobs of PRD section 21. The PoC has no authentication provider, so anonymous
/// access is either on or the API is closed.
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "DocReader:Security";

    /// <summary>
    /// When false, every /api/v1 request is answered with 503: there is no login to fall back to.
    /// </summary>
    public bool AllowAnonymousAccess { get; set; } = true;

    /// <summary>Origins allowed to call the API from a browser.</summary>
    public string[] AllowedCorsOrigins { get; set; } = [];
}
