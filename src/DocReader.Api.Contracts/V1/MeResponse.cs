namespace DocReader.Api.Contracts.V1;

/// <summary>
/// Identity of the caller, read from the bearer token's claims. In anonymous mode (no
/// <c>OIDC_AUTHORITY</c> configured) every field is <c>null</c> and <c>roles</c> is empty instead of the
/// endpoint answering 401, matching the rest of this anonymous PoC.
/// </summary>
/// <param name="UserId">The token's <c>sub</c> claim.</param>
/// <param name="Email">The token's <c>email</c> claim.</param>
/// <param name="Name">The token's <c>name</c> claim, or <c>preferred_username</c> when <c>name</c> is absent.</param>
/// <param name="Roles">Every role claim, including the ones flattened from Keycloak's <c>realm_access.roles</c>.</param>
public sealed record MeResponse(
    string? UserId,
    string? Email,
    string? Name,
    IReadOnlyList<string> Roles);
