namespace DocReader.Domain.Files;

public enum FileInspectionOutcome
{
    /// <summary>Signature matches one of the accepted document formats.</summary>
    Supported = 0,

    /// <summary>No known signature, or a known but unsupported document format.</summary>
    Unsupported = 1,

    /// <summary>Signature of an executable, archive or script disguised as a document.</summary>
    Blocked = 2,

    /// <summary>File has no bytes.</summary>
    Empty = 3
}
