namespace DocReader.Domain.Documents;

/// <summary>
/// Canonical event names written to the document timeline.
/// </summary>
public static class DocumentEventTypes
{
    public const string Received = "RECEIVED";
    public const string Stored = "STORED";
    public const string Queued = "QUEUED";
    public const string Rejected = "REJECTED";
    public const string Deleted = "DELETED";
    public const string Failed = "FAILED";
    public const string Purged = "PURGED";
    public const string RetentionApplied = "RETENTION_APPLIED";

    /// <summary>A retention policy changed and this document's <c>expiresAt</c> was recalculated to match.</summary>
    public const string RetentionPolicyReapplied = "RETENTION_POLICY_REAPPLIED";

    /// <summary>A webhook subscriber never accepted the notification of this document, after every retry.</summary>
    public const string WebhookDeliveryFailed = "WEBHOOK_DELIVERY_FAILED";

    public const string PreprocessingStarted = "PREPROCESSING_STARTED";
    public const string OcrStarted = "OCR_STARTED";
    public const string OcrPageCompleted = "OCR_PAGE_COMPLETED";
    public const string Classified = "CLASSIFIED";
    public const string ClassificationStarted = "CLASSIFICATION_STARTED";
    public const string ExtractionStarted = "EXTRACTION_STARTED";
    public const string Completed = "COMPLETED";
    public const string RetryScheduled = "RETRY_SCHEDULED";

    /// <summary>Reclassification and extraction were requested against the current document-type rules.</summary>
    public const string ReclassificationTriggered = "RECLASSIFICATION_TRIGGERED";

    /// <summary>A PDF page's text came from its own text layer (pdfplumber), so OCR was skipped for it (RF-009).</summary>
    public const string TextExtractedFromPdfNativeLayer = "TEXT_EXTRACTED_FROM_PDF_NATIVE_LAYER";

    /// <summary>A page was rotated 90, 180 or 270 degrees before OCR read it.</summary>
    public const string DocumentRotated = "DOCUMENT_ROTATED";

    /// <summary>A page had a small tilt straightened before OCR read it.</summary>
    public const string DocumentDeskewed = "DOCUMENT_DESKEWED";

    /// <summary>A page was reread with PP-StructureV3 instead of PP-OCRv5 (a suspected table, opt-in).</summary>
    public const string OcrReprocessedWithPpStructureV3 = "OCR_REPROCESSED_WITH_PP_STRUCTUREV3";

    /// <summary>The document's file was copied to another storage repository and StorageRepositoryId now points at it.</summary>
    public const string StorageMigrated = "STORAGE_MIGRATED";
}
