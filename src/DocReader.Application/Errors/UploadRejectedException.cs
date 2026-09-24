namespace DocReader.Application.Errors;

/// <summary>
/// Upload refused before the document was accepted. Nothing is persisted and nothing is stored.
/// </summary>
public sealed class UploadRejectedException : Exception
{
    public UploadRejectedException(UploadRejectionReason reason, string errorCode, string message)
        : base(message)
    {
        Reason = reason;
        ErrorCode = errorCode;
    }

    public UploadRejectionReason Reason { get; }

    /// <summary>Stable machine readable code, also used as the problem type suffix.</summary>
    public string ErrorCode { get; }
}
