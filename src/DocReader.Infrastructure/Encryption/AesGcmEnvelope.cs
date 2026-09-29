using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocReader.Infrastructure.Encryption;

/// <summary>
/// AES-256-GCM framing shared by every secret protector in the PoC (storage repository connection settings,
/// extracted field values, ...). Each protector owns its own key and its own error message; this type only owns
/// the envelope, so two hand-rolled AES-GCM implementations never drift apart. The envelope is a small JSON
/// document (version, nonce, ciphertext, tag), so it fits a <c>jsonb</c> or <c>text</c> column and shows nothing
/// about the plaintext. Every value gets a fresh random nonce; a wrong key or a tampered envelope fails
/// authentication instead of yielding garbage.
/// </summary>
internal static class AesGcmEnvelope
{
    private const int Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static string Protect(byte[] key, string plainText)
    {
        var plain = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        return JsonSerializer.Serialize(new Envelope(Version, "AES-256-GCM", Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag)));
    }

    /// <param name="key">The AES-256 key, exactly 32 bytes.</param>
    /// <param name="protectedText">The envelope, exactly as <see cref="Protect"/> produced it.</param>
    /// <param name="errorMessage">Used for every failure mode (damaged envelope, unknown version, wrong key, tampered
    /// ciphertext) so the caller controls what it says without this type knowing which secret it is protecting.</param>
    public static string Unprotect(byte[] key, string protectedText, string errorMessage)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(protectedText)
                ?? throw new InvalidOperationException(errorMessage);

            if (envelope.V != Version)
            {
                throw new InvalidOperationException(errorMessage);
            }

            var nonce = Convert.FromBase64String(envelope.N);
            var cipher = Convert.FromBase64String(envelope.C);
            var tag = Convert.FromBase64String(envelope.T);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);

            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException or ArgumentException)
        {
            throw new InvalidOperationException(errorMessage, exception);
        }
    }

    /// <summary>The stored form. The names are fixed on purpose: this is what sits in the database, so it must not follow a naming policy.</summary>
    private sealed record Envelope(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("alg")] string Alg,
        [property: JsonPropertyName("n")] string N,
        [property: JsonPropertyName("c")] string C,
        [property: JsonPropertyName("t")] string T);
}
