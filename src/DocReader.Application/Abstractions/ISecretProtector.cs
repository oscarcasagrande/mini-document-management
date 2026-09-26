namespace DocReader.Application.Abstractions;

/// <summary>
/// Encrypts the connection settings of a storage repository before they are stored, and decrypts them when an adapter
/// needs them. The stored form is a JSON envelope, so it fits a <c>jsonb</c> column.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts plain JSON text into the envelope that is stored.</summary>
    string Protect(string plainJson);

    /// <exception cref="InvalidOperationException">The envelope is damaged, or was made with another key.</exception>
    string Unprotect(string protectedJson);
}
