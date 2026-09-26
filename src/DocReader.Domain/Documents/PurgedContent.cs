namespace DocReader.Domain.Documents;

/// <summary>
/// What a purge removes, as named in the details of the <c>PURGED</c> timeline event. The document record itself
/// (protocol, type, timestamps, status, timeline, jobs) is never part of it: it stays as a tombstone.
/// </summary>
public static class PurgedContent
{
    /// <summary>The original file in its storage repository.</summary>
    public const string File = "file";

    /// <summary>The text the OCR read: the raw text, the page texts and the raw OCR payload.</summary>
    public const string OcrText = "ocr_text";

    /// <summary>The extracted fields and the structured result.</summary>
    public const string ExtractedFields = "extracted_fields";
}
