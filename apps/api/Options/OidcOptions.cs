using Microsoft.Extensions.Options;

namespace DocReader.Api.Options;

/// <summary>
/// Enterprise SSO for the API, read from the environment (<c>OIDC_AUTHORITY</c>, <c>OIDC_CLIENT_ID</c>,
/// <c>OIDC_ADMIN_ROLE</c>, <c>OIDC_USER_ROLE</c>). See ADR 0003 for why this is JWT bearer validation
/// and not <c>AddOpenIdConnect</c>: the browser only ever talks to the web-bff, never to this API.
/// </summary>
public sealed class OidcOptions
{
    public const string SectionName = "DocReader:Security:Oidc";

    /// <summary>Default role required by the admin endpoints (backup, restore, storage migration).</summary>
    public const string DefaultAdminRole = "docreader-admin";

    /// <summary>Default role granted to every authenticated user with ordinary access.</summary>
    public const string DefaultUserRole = "docreader-user";

    /// <summary>
    /// Issuer of the OIDC provider (e.g. a Keycloak realm URL). Empty is anonymous mode: no JWT bearer
    /// scheme is registered and <see cref="Http.AnonymousAccessGateMiddleware"/> keeps gating <c>/api</c>
    /// exactly as it always has.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    /// This API's client id at the provider. Also used as the expected token audience: a token whose
    /// <c>aud</c> does not contain it is rejected. Empty skips audience validation (Authority alone still
    /// validates the issuer and the signature).
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>Role required by <c>[Authorize(Policy = AuthorizationPolicies.AdminOnly)]</c>.</summary>
    public string AdminRole { get; set; } = DefaultAdminRole;

    /// <summary>
    /// Role granted to every ordinary authenticated user. Not enforced by any endpoint today: reserved
    /// for a future RBAC pass on the non-admin controllers (see CLAUDE.md).
    /// </summary>
    public string UserRole { get; set; } = DefaultUserRole;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority);
}

/// <summary>
/// Anonymous mode (empty <see cref="OidcOptions.Authority"/>) is a valid, intentional configuration: it
/// is the PoC default. Once an authority is set, it must be a real absolute URL and the admin role must
/// not be blank, or every request would fail a policy nobody could ever satisfy.
/// </summary>
public sealed class OidcOptionsValidator : IValidateOptions<OidcOptions>
{
    public ValidateOptionsResult Validate(string? name, OidcOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Authority))
        {
            return ValidateOptionsResult.Success;
        }

        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority) ||
            (authority.Scheme != Uri.UriSchemeHttp && authority.Scheme != Uri.UriSchemeHttps))
        {
            return ValidateOptionsResult.Fail(
                "DocReader:Security:Oidc:Authority (OIDC_AUTHORITY) must be empty (anonymous mode) or an " +
                "absolute http(s) URL, e.g. https://keycloak.local/realms/docreader.");
        }

        if (string.IsNullOrWhiteSpace(options.AdminRole))
        {
            return ValidateOptionsResult.Fail(
                "DocReader:Security:Oidc:AdminRole (OIDC_ADMIN_ROLE) cannot be empty once Authority is set: " +
                "no token could ever satisfy an empty role requirement.");
        }

        if (string.IsNullOrWhiteSpace(options.UserRole))
        {
            return ValidateOptionsResult.Fail(
                "DocReader:Security:Oidc:UserRole (OIDC_USER_ROLE) cannot be empty once Authority is set.");
        }

        return ValidateOptionsResult.Success;
    }
}
