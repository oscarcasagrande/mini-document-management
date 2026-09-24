namespace DocReader.Application.Errors;

/// <summary>
/// The file matched an accepted signature but could not be parsed, for example an encrypted or
/// truncated PDF.
/// </summary>
public sealed class UnreadableDocumentException(string message, Exception? innerException = null)
    : Exception(message, innerException);
