using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using Microsoft.Extensions.Options;

namespace DocReader.Infrastructure.Storage;

/// <summary>
/// AES-256-GCM. The envelope is a small JSON document (version, nonce, ciphertext, tag), so it sits in a <c>jsonb</c> column
/// and shows nothing about the settings. Every value gets a fresh random nonce; a wrong key or a tampered envelope fails
/// authentication instead of yielding garbage.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const int Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesGcmSecretProtector(IOptions<SecretsOptions> options)
    {
        _key = Convert.FromBase64String(options.Value.EncryptionKey);
    }

    public string Protect(string plainJson)
    {
        var plain = Encoding.UTF8.GetBytes(plainJson);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        return JsonSerializer.Serialize(new Envelope(Version, "AES-256-GCM", Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), Convert.ToBase64String(tag)));
    }

    public string Unprotect(string protectedJson)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(protectedJson)
                ?? throw new InvalidOperationException("The stored settings are not readable.");

            if (envelope.V != Version)
            {
                throw new InvalidOperationException($"The stored settings use an unknown format version ({envelope.V}).");
            }

            var nonce = Convert.FromBase64String(envelope.N);
            var cipher = Convert.FromBase64String(envelope.C);
            var tag = Convert.FromBase64String(envelope.T);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);

            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException or ArgumentException)
        {
            throw new InvalidOperationException(
                "The connection settings of a storage repository could not be decrypted. The encryption key is not the one they were saved with, or the data is damaged.",
                exception);
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
