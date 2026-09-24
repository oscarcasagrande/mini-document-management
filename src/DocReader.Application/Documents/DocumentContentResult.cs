namespace DocReader.Application.Documents;

/// <summary>
/// Original bytes of a document, ready to be streamed to the client.
/// </summary>
/// <param name="Content">Seekable stream positioned at the beginning.</param>
/// <param name="MimeType">Detected MIME type stored at upload time.</param>
/// <param name="FileName">Sanitized original file name, safe for a Content-Disposition header.</param>
/// <param name="SizeBytes">Size in bytes.</param>
/// <param name="Sha256">Hash of the stored bytes, usable as a strong ETag.</param>
public sealed record DocumentContentResult(
    Stream Content,
    string MimeType,
    string FileName,
    long SizeBytes,
    string Sha256) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
