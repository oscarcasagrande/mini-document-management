using Microsoft.AspNetCore.Authorization;

namespace DocReader.Api.Security;

/// <summary>
/// Policy names used by <c>[Authorize(Policy = ...)]</c>. The role each policy actually requires is
/// configured once, from <see cref="Options.OidcOptions"/>, when the policies are registered in
/// <c>Program.cs</c> — never hardcoded again at the attribute site.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// Required on the three admin controllers (backup, restore, storage migration). In anonymous mode
    /// (no <c>OIDC_AUTHORITY</c>) this policy is registered as an always-succeeding assertion, so those
    /// controllers keep working without a token exactly as before.
    /// </summary>
    public const string AdminOnly = "AdminOnly";

    /// <summary>
    /// Builds the <see cref="AdminOnly"/> policy. Pulled out of <c>Program.cs</c> so the two branches —
    /// "no OIDC, always allow" and "OIDC configured, require the admin role" — are unit testable directly
    /// against a real <see cref="IAuthorizationService"/>, without a TestServer.
    /// </summary>
    public static void ConfigureAdminOnly(AuthorizationOptions options, bool oidcConfigured, string adminRole)
    {
        if (oidcConfigured)
        {
            options.AddPolicy(AdminOnly, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(adminRole));
        }
        else
        {
            options.AddPolicy(AdminOnly, policy => policy.RequireAssertion(_ => true));
        }
    }
}
