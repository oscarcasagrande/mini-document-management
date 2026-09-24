namespace DocReader.Application.Errors;

/// <summary>
/// A query string filter carries a value outside its allowed set. Maps to 400.
/// </summary>
public sealed class InvalidFilterException(string fieldName, string message) : Exception(message)
{
    public string FieldName { get; } = fieldName;
}
