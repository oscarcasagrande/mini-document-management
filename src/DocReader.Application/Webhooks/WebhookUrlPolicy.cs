using System.Net;
using DocReader.Application.Errors;
using DocReader.Domain.Webhooks;

namespace DocReader.Application.Webhooks;

/// <summary>
/// What a webhook URL may be: absolute http or https, no credentials in it, and (unless private networks are allowed) not a
/// place inside the network. The check on the name is a first line; the sender checks the addresses it actually connects to.
/// </summary>
public static class WebhookUrlPolicy
{
    /// <returns>The URL as it will be stored: trimmed, and in its canonical absolute form.</returns>
    /// <exception cref="RequestValidationException">The URL is not acceptable.</exception>
    public static string Validate(string? url, bool allowPrivateNetworks)
    {
        var trimmed = url?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > WebhookSubscription.MaxUrlLength)
        {
            throw Invalid($"The url is required and must not exceed {WebhookSubscription.MaxUrlLength} characters.");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw Invalid("The url must be an absolute http or https URL.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw Invalid("The url must not contain credentials: sign with the secret instead.");
        }

        if (!allowPrivateNetworks && IsPrivateTarget(uri.Host))
        {
            throw Invalid(
                "The url points at a local or private address, which webhooks may not reach. Use a public address, or set WEBHOOK_ALLOW_PRIVATE_NETWORKS=true to test against a receiver on your own network.");
        }

        return uri.AbsoluteUri;
    }

    private static bool IsPrivateTarget(string host) =>
        IPAddress.TryParse(host.Trim('[', ']'), out var address)
            ? PrivateNetwork.IsPrivate(address)
            : PrivateNetwork.IsLocalName(host);

    private static RequestValidationException Invalid(string message) => new("INVALID_WEBHOOK_URL", message);
}
