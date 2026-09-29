using System.Security.Claims;
using DocReader.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DocReader.UnitTests.Api;

/// <summary>
/// The three admin controllers carry <c>[Authorize(Policy = AuthorizationPolicies.AdminOnly)]</c>
/// regardless of whether OIDC is configured, so the policy itself must behave correctly in both modes:
/// an always-succeeding assertion in anonymous mode (proving the admin endpoints still work without a
/// token), and a real role requirement once OIDC is on. Evaluated against a real
/// <see cref="IAuthorizationService"/>, without a TestServer.
/// </summary>
public sealed class AuthorizationPoliciesTests
{
    [Fact]
    public async Task Sem_oidc_a_policy_AdminOnly_aceita_chamador_anonimo()
    {
        var authorizationService = BuildAuthorizationService(oidcConfigured: false, adminRole: "docreader-admin");
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await authorizationService.AuthorizeAsync(anonymous, AuthorizationPolicies.AdminOnly);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Com_oidc_a_policy_AdminOnly_recusa_usuario_autenticado_sem_o_papel()
    {
        var authorizationService = BuildAuthorizationService(oidcConfigured: true, adminRole: "docreader-admin");
        var user = AuthenticatedUser("docreader-user");

        var result = await authorizationService.AuthorizeAsync(user, AuthorizationPolicies.AdminOnly);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Com_oidc_a_policy_AdminOnly_recusa_chamador_anonimo()
    {
        var authorizationService = BuildAuthorizationService(oidcConfigured: true, adminRole: "docreader-admin");
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await authorizationService.AuthorizeAsync(anonymous, AuthorizationPolicies.AdminOnly);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Com_oidc_a_policy_AdminOnly_aceita_usuario_com_o_papel_configurado()
    {
        var authorizationService = BuildAuthorizationService(oidcConfigured: true, adminRole: "docreader-admin");
        var user = AuthenticatedUser("docreader-admin");

        var result = await authorizationService.AuthorizeAsync(user, AuthorizationPolicies.AdminOnly);

        Assert.True(result.Succeeded);
    }

    private static ClaimsPrincipal AuthenticatedUser(string role)
    {
        var identity = new ClaimsIdentity(authenticationType: "Bearer");
        identity.AddClaim(new Claim(ClaimTypes.Role, role));

        return new ClaimsPrincipal(identity);
    }

    private static IAuthorizationService BuildAuthorizationService(bool oidcConfigured, string adminRole)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore(options =>
            AuthorizationPolicies.ConfigureAdminOnly(options, oidcConfigured, adminRole));

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }
}
