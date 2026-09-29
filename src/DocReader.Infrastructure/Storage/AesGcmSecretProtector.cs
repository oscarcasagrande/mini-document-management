using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using DocReader.Infrastructure.Encryption;
using Microsoft.Extensions.Options;

namespace DocReader.Infrastructure.Storage;

/// <summary>
/// AES-256-GCM, keyed by <c>STORAGE_CONFIG_ENCRYPTION_KEY</c>. The envelope is a small JSON document (version,
/// nonce, ciphertext, tag; framing shared with every other protector in <see cref="AesGcmEnvelope"/>), so it sits
/// in a <c>jsonb</c> column and shows nothing about the settings. Every value gets a fresh random nonce; a wrong
/// key or a tampered envelope fails authentication instead of yielding garbage.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string DecryptionErrorMessage =
        "The connection settings of a storage repository could not be decrypted. The encryption key is not the one they were saved with, or the data is damaged.";

    private readonly byte[] _key;

    public AesGcmSecretProtector(IOptions<SecretsOptions> options)
    {
        _key = Convert.FromBase64String(options.Value.EncryptionKey);
    }

    public string Protect(string plainJson) => AesGcmEnvelope.Protect(_key, plainJson);

    public string Unprotect(string protectedJson) => AesGcmEnvelope.Unprotect(_key, protectedJson, DecryptionErrorMessage);
}
