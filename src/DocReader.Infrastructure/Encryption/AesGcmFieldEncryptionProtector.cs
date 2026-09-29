using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using Microsoft.Extensions.Options;

namespace DocReader.Infrastructure.Encryption;

/// <summary>
/// AES-256-GCM, keyed by <c>EXTRACTED_FIELD_ENCRYPTION_KEY</c>. Same envelope shape as
/// <see cref="DocReader.Infrastructure.Storage.AesGcmSecretProtector"/> (shared in <see cref="AesGcmEnvelope"/>),
/// a different key so the two secrets can be rotated independently.
/// </summary>
public sealed class AesGcmFieldEncryptionProtector : IFieldEncryptionProtector
{
    private const string DecryptionErrorMessage =
        "An extracted field value could not be decrypted. The encryption key is not the one it was saved with, or the data is damaged.";

    private readonly byte[] _key;

    public AesGcmFieldEncryptionProtector(IOptions<FieldEncryptionOptions> options)
    {
        _key = Convert.FromBase64String(options.Value.EncryptionKey);
    }

    public string Protect(string plainText) => AesGcmEnvelope.Protect(_key, plainText);

    public string Unprotect(string protectedText) => AesGcmEnvelope.Unprotect(_key, protectedText, DecryptionErrorMessage);
}
