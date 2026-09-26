using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DocReader.Application.Options;

/// <summary>Key that encrypts the connection settings of the storage repositories. API and worker must share it.</summary>
public sealed class SecretsOptions
{
    public const string SectionName = "DocReader:Secrets";

    /// <summary>Base64 of 32 random bytes (AES-256). Generate one with <c>openssl rand -base64 32</c>.</summary>
    [Required]
    public string EncryptionKey { get; set; } = string.Empty;
}

/// <summary>Stops the service at startup when the key is missing, is not Base64 or is not 32 bytes: better than failing on the first secret.</summary>
public sealed class SecretsOptionsValidator : IValidateOptions<SecretsOptions>
{
    public ValidateOptionsResult Validate(string? name, SecretsOptions options)
    {
        try
        {
            if (Convert.FromBase64String(options.EncryptionKey).Length == 32)
            {
                return ValidateOptionsResult.Success;
            }
        }
        catch (FormatException)
        {
            // Falls through to the message below.
        }

        return ValidateOptionsResult.Fail(
            "DocReader:Secrets:EncryptionKey (STORAGE_CONFIG_ENCRYPTION_KEY) must be the Base64 of exactly 32 bytes. Generate one with: openssl rand -base64 32");
    }
}
