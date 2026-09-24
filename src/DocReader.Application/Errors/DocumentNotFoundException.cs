namespace DocReader.Application.Errors;

/// <summary>
/// No document matches the requested identity.
/// </summary>
public sealed class DocumentNotFoundException(string identifier)
    : Exception($"Document {identifier} was not found.")
{
    public string Identifier { get; } = identifier;
}
