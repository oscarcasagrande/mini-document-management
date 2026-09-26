using System.Security.Cryptography;
using System.Text;

namespace DocReader.Application.Webhooks;

/// <summary>
/// The signature of a notification: HMAC-SHA256 of the exact body bytes, keyed with the secret of the subscription, in lower case
/// hexadecimal and prefixed with <c>sha256=</c>. A receiver recomputes it over the raw body it received and compares in constant time.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-Webhook-Signature";
    public const string Prefix = "sha256=";

    public static string Compute(string secret, byte[] body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);

        return Prefix + Convert.ToHexStringLower(hash);
    }

    /// <summary>Constant-time check, for tests and for receivers written in .NET.</summary>
    public static bool Verify(string secret, byte[] body, string? signature)
    {
        if (signature is null)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Compute(secret, body));
        var received = Encoding.ASCII.GetBytes(signature.Trim());

        return CryptographicOperations.FixedTimeEquals(expected, received);
    }
}
