namespace DocReader.Application.Abstractions;

/// <summary>
/// Encrypts the raw and normalized value of an extracted field before it is stored, and decrypts it back when
/// it is read. Keyed by <c>EXTRACTED_FIELD_ENCRYPTION_KEY</c>, separate from <see cref="ISecretProtector"/> and
/// its <c>STORAGE_CONFIG_ENCRYPTION_KEY</c>, so the two can be rotated independently. Consumed by the EF value
/// converter on <c>ExtractedField.RawValue</c>/<c>NormalizedValue</c>; nothing else should need it.
/// </summary>
public interface IFieldEncryptionProtector
{
    /// <summary>Encrypts plain text into the envelope that is stored.</summary>
    string Protect(string plainText);

    /// <exception cref="InvalidOperationException">The envelope is damaged, or was made with another key.</exception>
    string Unprotect(string protectedText);
}
