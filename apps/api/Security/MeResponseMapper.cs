using System.Security.Claims;
using DocReader.Api.Contracts.V1;

namespace DocReader.Api.Security;

/// <summary>Builds the response of <c>GET /api/v1/me</c> from the authenticated principal's claims.</summary>
public static class MeResponseMapper
{
    /// <summary>
    /// Fixed payload for anonymous mode (no <c>OIDC_AUTHORITY</c> configured): there is no token to read,
    /// so every field is empty instead of guessing an identity.
    /// </summary>
    public static readonly MeResponse Anonymous = new(null, null, null, []);

    public static MeResponse Map(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return Anonymous;
        }

        var userId = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = user.FindFirstValue("email") ?? user.FindFirstValue(ClaimTypes.Email);
        var name = user.FindFirstValue("name")
            ?? user.FindFirstValue("preferred_username")
            ?? user.FindFirstValue(ClaimTypes.Name);

        var roles = user.FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new MeResponse(userId, email, name, roles);
    }
}
