using System.Security.Cryptography;
using System.Text;
using DocReader.Application.Options;
using Microsoft.Extensions.Options;

namespace DocReader.Infrastructure.Backup;

/// <summary>
/// Signs the checksum list of a backup with HMAC-SHA256, under a key derived (HKDF) from the installation's encryption
/// key (<c>STORAGE_CONFIG_ENCRYPTION_KEY</c>). A restore replays arbitrary SQL with the application's database role, so
/// checksums alone are not enough: anyone can compute a SHA-256. The signature makes the API accept only archives made
/// by an installation that holds the same key, which a restore already needs anyway, because the storage repository
/// settings inside the dump are encrypted with it.
/// </summary>
public sealed class BackupSignature
{
    private const string Prefix = "hmac-sha256 ";
    private static readonly byte[] DerivationInfo = Encoding.ASCII.GetBytes("docreader/backup-archive-signature/v1");

    private readonly byte[] _key;

    public BackupSignature(IOptions<SecretsOptions> options)
    {
        var master = Convert.FromBase64String(options.Value.EncryptionKey);
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: [], info: DerivationInfo);
    }

    public string Sign(ReadOnlySpan<byte> checksums) =>
        Prefix + Convert.ToHexStringLower(HMACSHA256.HashData(_key, checksums)) + "\n";

    public bool Verify(ReadOnlySpan<byte> checksums, string signatureFile)
    {
        var text = signatureFile.Trim();
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(text[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(presented, HMACSHA256.HashData(_key, checksums));
    }
}
