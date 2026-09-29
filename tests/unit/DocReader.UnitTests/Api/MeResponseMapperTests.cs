using System.Security.Claims;
using DocReader.Api.Security;
using Xunit;

namespace DocReader.UnitTests.Api;

public sealed class MeResponseMapperTests
{
    [Fact]
    public void Usuario_nao_autenticado_recebe_o_payload_anonimo_fixo()
    {
        var response = MeResponseMapper.Map(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Null(response.UserId);
        Assert.Null(response.Email);
        Assert.Null(response.Name);
        Assert.Empty(response.Roles);
    }

    [Fact]
    public void Usuario_autenticado_mapeia_sub_email_e_name()
    {
        var identity = new ClaimsIdentity(authenticationType: "Bearer");
        identity.AddClaim(new Claim("sub", "11111111-1111-1111-1111-111111111111"));
        identity.AddClaim(new Claim("email", "operador@example.com"));
        identity.AddClaim(new Claim("name", "Operador de Teste"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "docreader-admin"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "docreader-user"));

        var response = MeResponseMapper.Map(new ClaimsPrincipal(identity));

        Assert.Equal("11111111-1111-1111-1111-111111111111", response.UserId);
        Assert.Equal("operador@example.com", response.Email);
        Assert.Equal("Operador de Teste", response.Name);
        Assert.Equal(["docreader-admin", "docreader-user"], response.Roles.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Sem_claim_name_cai_para_preferred_username()
    {
        var identity = new ClaimsIdentity(authenticationType: "Bearer");
        identity.AddClaim(new Claim("sub", "11111111-1111-1111-1111-111111111111"));
        identity.AddClaim(new Claim("preferred_username", "operador"));

        var response = MeResponseMapper.Map(new ClaimsPrincipal(identity));

        Assert.Equal("operador", response.Name);
    }

    [Fact]
    public void Papeis_duplicados_aparecem_uma_unica_vez()
    {
        var identity = new ClaimsIdentity(authenticationType: "Bearer");
        identity.AddClaim(new Claim("sub", "11111111-1111-1111-1111-111111111111"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "docreader-admin"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "docreader-admin"));

        var response = MeResponseMapper.Map(new ClaimsPrincipal(identity));

        Assert.Single(response.Roles);
    }
}
