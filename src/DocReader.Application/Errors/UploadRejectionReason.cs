namespace DocReader.Application.Errors;

/// <summary>
/// Why an upload was refused. Each reason maps to one HTTP status in the API layer.
/// </summary>
public enum UploadRejectionReason
{
    /// <summary>No file part, or a file with zero bytes. Maps to 400.</summary>
    MissingFile = 0,

    /// <summary>Above the configured size limit. Maps to 413.</summary>
    TooLarge = 1,

    /// <summary>Signature is not one of the accepted formats. Maps to 415.</summary>
    UnsupportedFormat = 2,

    /// <summary>Declared content type contradicts the detected signature. Maps to 415.</summary>
    DeclaredTypeMismatch = 3,

    /// <summary>Executable, archive or script disguised as a document. Maps to 415.</summary>
    BlockedContent = 4,

    /// <summary>Accepted format but unreadable or above the page limit. Maps to 422.</summary>
    UnprocessableContent = 5,

    /// <summary>A field of the request is invalid. Maps to 400.</summary>
    InvalidRequest = 6
}
