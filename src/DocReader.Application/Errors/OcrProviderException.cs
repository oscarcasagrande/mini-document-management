namespace DocReader.Application.Errors;

/// <summary>
/// The OCR provider could not read the document. <see cref="IsTransient"/> decides between a retry
/// with backoff and a definitive failure; the message is safe to show to an operator and never
/// carries document content.
/// </summary>
public sealed class OcrProviderException(string code, string message, bool isTransient, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;

    public bool IsTransient { get; } = isTransient;
}
