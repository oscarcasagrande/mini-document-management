using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DocReader.Api.Security;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace DocReader.UnitTests.Api;

/// <summary>
/// Keycloak nests roles under a <c>realm_access</c> claim shaped as <c>{"roles": [...]}</c>, which
/// ASP.NET Core's default JWT bearer handling has no idea carries roles. This builds and validates a real
/// JWT (as <c>System.IdentityModel.Tokens.Jwt</c> would after <c>AddJwtBearer</c> runs), proving
/// <see cref="ClaimsPrincipal.IsInRole"/> only starts working once <see cref="KeycloakRoleClaims"/>
/// expands that claim — the crux of RBAC working at all against Keycloak.
/// </summary>
public sealed class KeycloakRoleClaimsTests
{
    private const string Issuer = "https://keycloak.local/realms/docreader";
    private const string Audience = "docreader-api";

    [Fact]
    public void Jwt_com_realm_access_roles_so_habilita_IsInRole_apos_a_expansao()
    {
        var principal = ValidatePrincipalFromToken(BuildSignedToken(
        [
            new Claim("sub", "11111111-1111-1111-1111-111111111111"),
            new Claim("realm_access", """{"roles":["offline_access","docreader-admin"]}""", JsonClaimValueTypes.Json)
        ]));

        // Before the transformation this repository registers only when OIDC is configured, the default
        // JWT handling has no idea realm_access carries roles.
        Assert.False(principal.IsInRole("docreader-admin"));

        ExpandRolesInPlace(principal);

        Assert.True(principal.IsInRole("docreader-admin"));
        Assert.False(principal.IsInRole("nao-existe"));
    }

    [Fact]
    public void ExpandRealmRoles_ignora_realm_access_sem_array_de_roles()
    {
        var claims = new[] { new Claim("realm_access", """{"not-roles":[]}""") };

        Assert.Empty(KeycloakRoleClaims.ExpandRealmRoles(claims));
    }

    [Fact]
    public void ExpandRealmRoles_ignora_json_invalido_sem_lancar()
    {
        var claims = new[] { new Claim("realm_access", "not-json") };

        Assert.Empty(KeycloakRoleClaims.ExpandRealmRoles(claims));
    }

    [Fact]
    public void ExpandRealmRoles_ignora_claims_de_outro_tipo()
    {
        var claims = new[] { new Claim("email", "operador@example.com") };

        Assert.Empty(KeycloakRoleClaims.ExpandRealmRoles(claims));
    }

    private static void ExpandRolesInPlace(ClaimsPrincipal principal)
    {
        // Microsoft.IdentityModel.Tokens.CaseSensitiveClaimsIdentity, which JwtSecurityTokenHandler
        // produces by default: a ClaimsIdentity subclass, not the base type itself.
        var identity = Assert.IsAssignableFrom<ClaimsIdentity>(principal.Identity);

        foreach (var role in KeycloakRoleClaims.ExpandRealmRoles(identity.Claims).ToArray())
        {
            identity.AddClaim(role);
        }
    }

    private static string BuildSignedToken(IEnumerable<Claim> claims)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("unit-test-only-signing-key-at-least-32-bytes-long"));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static ClaimsPrincipal ValidatePrincipalFromToken(string jwt)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("unit-test-only-signing-key-at-least-32-bytes-long"));

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = true,
            IssuerSigningKey = key
        };

        return new JwtSecurityTokenHandler().ValidateToken(jwt, validationParameters, out _);
    }
}
