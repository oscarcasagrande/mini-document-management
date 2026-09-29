namespace DocReader.Application.Errors;

/// <summary>An uploaded backup archive that cannot be trusted: unreadable, tampered with, incomplete or not signed with this installation's key (400).</summary>
public sealed class BackupArchiveInvalidException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
