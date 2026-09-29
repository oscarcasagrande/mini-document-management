using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace DocReader.Api.Security;

/// <summary>
/// Keycloak (and most realm-based IdPs) nest a user's roles under a <c>realm_access</c> claim shaped as
/// <c>{"roles": ["docreader-admin", ...]}</c>, not as one claim per role. ASP.NET Core's default JWT
/// handling has no idea that claim carries roles, so <see cref="ClaimsPrincipal.IsInRole"/> and
/// <c>[Authorize(Roles = ...)]</c> silently never match unless this is flattened first.
/// </summary>
public static class KeycloakRoleClaims
{
    public const string RealmAccessClaimType = "realm_access";

    /// <summary>
    /// Reads every <c>realm_access</c> claim in <paramref name="claims"/> and yields one
    /// <see cref="ClaimTypes.Role"/> claim per entry of its <c>roles</c> array. A claim that is missing,
    /// not valid JSON, or has no <c>roles</c> array yields nothing rather than throwing: a token from a
    /// provider that does not use this shape should not be treated as an error.
    /// </summary>
    public static IEnumerable<Claim> ExpandRealmRoles(IEnumerable<Claim> claims)
    {
        foreach (var claim in claims)
        {
            if (!string.Equals(claim.Type, RealmAccessClaimType, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var role in ReadRoles(claim))
            {
                yield return new Claim(ClaimTypes.Role, role, ClaimValueTypes.String, claim.Issuer);
            }
        }
    }

    private static IEnumerable<string> ReadRoles(Claim realmAccessClaim)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(realmAccessClaim.Value);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("roles", out var roles) ||
                roles.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var role in roles.EnumerateArray())
            {
                if (role.ValueKind == JsonValueKind.String && role.GetString() is { Length: > 0 } value)
                {
                    yield return value;
                }
            }
        }
    }
}

/// <summary>
/// Adds the flattened <c>realm_access.roles</c> claims to the authenticated principal after JWT
/// validation. Registered only when OIDC is configured. ASP.NET Core may invoke
/// <see cref="TransformAsync"/> more than once per request, so it skips roles already present instead of
/// duplicating them.
/// </summary>
public sealed class KeycloakRealmRolesClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
        {
            return Task.FromResult(principal);
        }

        foreach (var role in KeycloakRoleClaims.ExpandRealmRoles(identity.Claims).ToArray())
        {
            if (!identity.HasClaim(role.Type, role.Value))
            {
                identity.AddClaim(role);
            }
        }

        return Task.FromResult(principal);
    }
}
