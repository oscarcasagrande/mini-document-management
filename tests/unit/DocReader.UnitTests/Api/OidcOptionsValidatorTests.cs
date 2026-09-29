using DocReader.Api.Options;
using Xunit;

namespace DocReader.UnitTests.Api;

/// <summary>
/// Anonymous mode (empty Authority) has to stay a valid, accepted configuration — it is the PoC
/// default — while a non-empty Authority that is not a real URL must fail fast at startup instead of
/// producing a JWT bearer scheme nothing can ever validate against.
/// </summary>
public sealed class OidcOptionsValidatorTests
{
    private static readonly OidcOptionsValidator Validator = new();

    [Fact]
    public void Authority_vazia_e_aceita_como_modo_anonimo()
    {
        var result = Validator.Validate(null, new OidcOptions { Authority = null });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Authority_em_branco_e_aceita_como_modo_anonimo()
    {
        var result = Validator.Validate(null, new OidcOptions { Authority = "   " });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Authority_valida_e_aceita()
    {
        var result = Validator.Validate(null, new OidcOptions
        {
            Authority = "https://keycloak.local/realms/docreader"
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Authority_que_nao_e_uma_url_absoluta_e_recusada_com_mensagem_clara()
    {
        var result = Validator.Validate(null, new OidcOptions { Authority = "not-a-url" });

        Assert.True(result.Failed);
        Assert.Contains("OIDC_AUTHORITY", result.FailureMessage);
    }

    [Fact]
    public void Authority_com_esquema_nao_http_e_recusada()
    {
        var result = Validator.Validate(null, new OidcOptions { Authority = "ftp://keycloak.local/realms/docreader" });

        Assert.True(result.Failed);
    }

    [Fact]
    public void AdminRole_vazio_com_authority_configurada_e_recusado()
    {
        var result = Validator.Validate(null, new OidcOptions
        {
            Authority = "https://keycloak.local/realms/docreader",
            AdminRole = " "
        });

        Assert.True(result.Failed);
        Assert.Contains("OIDC_ADMIN_ROLE", result.FailureMessage);
    }

    [Fact]
    public void UserRole_vazio_com_authority_configurada_e_recusado()
    {
        var result = Validator.Validate(null, new OidcOptions
        {
            Authority = "https://keycloak.local/realms/docreader",
            UserRole = " "
        });

        Assert.True(result.Failed);
        Assert.Contains("OIDC_USER_ROLE", result.FailureMessage);
    }
}
