using System.Text.Json;
using DocReader.Application.Abstractions;
using DocReader.Application.Classification;
using DocReader.Application.Errors;
using DocReader.Application.Extraction;
using DocReader.Domain.Documents;
using Microsoft.Extensions.Logging;

namespace DocReader.Application.Documents;

/// <summary>
/// Read side of the API: listing, detail, status and original content. All of it works without
/// login (RF-005, RF-006) and stays available even when processing failed.
/// </summary>
public sealed class DocumentQueryService(
    IDocumentRepository repository,
    IFileStorage storage,
    IDocumentClassifier classifier,
    IEnumerable<IDocumentExtractor> extractors,
    ILogger<DocumentQueryService> logger)
{
    /// <summary>Length of the <c>external_reference</c> column; nothing longer can exist.</summary>
    private const int MaxStoredExternalReferenceLength = 256;

    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web);

    public Task<PagedResult<Document>> ListAsync(DocumentListFilter filter, CancellationToken ct) =>
        repository.ListAsync(filter, ct);

    public async Task<Document> GetByIdAsync(Guid id, bool includeEvents, CancellationToken ct)
    {
        var document = await repository.FindByIdAsync(id, includeEvents, ct).ConfigureAwait(false);
        return document ?? throw new DocumentNotFoundException(id.ToString());
    }

    public async Task<Document> GetByProtocolAsync(string protocol, bool includeEvents, CancellationToken ct)
    {
        if (!DocumentProtocol.IsWellFormed(protocol))
        {
            throw new DocumentNotFoundException(protocol);
        }

        var document = await repository.FindByProtocolAsync(protocol, includeEvents, ct).ConfigureAwait(false);
        return document ?? throw new DocumentNotFoundException(protocol);
    }

    /// <summary>
    /// The most recent document uploaded with exactly this external reference. The reference is the caller's own
    /// key and is not unique: when several documents share it, the latest upload wins.
    /// </summary>
    /// <exception cref="DocumentNotFoundException">The reference is blank, longer than any stored one, or unused.</exception>
    public async Task<Document> GetLatestByExternalReferenceAsync(string reference, CancellationToken ct)
    {
        var trimmed = reference.Trim();
        var label = $"with external reference '{(trimmed.Length > 64 ? trimmed[..64] + "...": trimmed)}'";

        if (trimmed.Length == 0 || trimmed.Length > MaxStoredExternalReferenceLength)
        {
            throw new DocumentNotFoundException(label);
        }

        var document = await repository.FindLatestByExternalReferenceAsync(trimmed, ct).ConfigureAwait(false);
        return document ?? throw new DocumentNotFoundException(label);
    }

    /// <summary>
    /// The document with its latest job and extraction summary: what the status and detail views need
    /// to show progress, the last error and the identity of the result.
    /// </summary>
    public async Task<DocumentSnapshot> GetSnapshotAsync(Guid id, bool includeEvents, CancellationToken ct)
    {
        var document = await GetByIdAsync(id, includeEvents, ct).ConfigureAwait(false);
        var job = await repository.FindLatestJobAsync(id, ct).ConfigureAwait(false);
        var extraction = await repository.FindLatestExtractionSummaryAsync(id, ct).ConfigureAwait(false);

        return new DocumentSnapshot(document, job, extraction);
    }

    /// <summary>
    /// The structured result of the latest extraction. While a reprocessing runs, the previous result
    /// stays available (RF-013); the document status in the answer says what is happening now.
    /// </summary>
    /// <exception cref="ResultNotReadyException">There is no extraction yet, or every attempt failed.</exception>
    public async Task<DocumentResult> GetResultAsync(Guid id, CancellationToken ct)
    {
        var document = await GetByIdAsync(id, includeEvents: false, ct).ConfigureAwait(false);
        var result = await repository.FindLatestExtractionResultAsync(id, ct).ConfigureAwait(false);

        return result is null
            ? throw new ResultNotReadyException(id, document.Status)
            : new DocumentResult(document, result);
    }

    /// <summary>The raw text, page by page, of the latest extraction.</summary>
    /// <exception cref="ResultNotReadyException">There is no extraction yet, or every attempt failed.</exception>
    public async Task<DocumentText> GetTextAsync(Guid id, CancellationToken ct)
    {
        var document = await GetByIdAsync(id, includeEvents: false, ct).ConfigureAwait(false);
        var text = await repository.FindLatestExtractionTextAsync(id, ct).ConfigureAwait(false);

        return text is null
            ? throw new ResultNotReadyException(id, document.Status)
            : new DocumentText(document, text);
    }

    /// <summary>
    /// Explains the classification of the latest extraction: the text as the OCR read it and, for every
    /// document type, the score and the evidence found or missing, using the rules currently in force.
    /// </summary>
    /// <exception cref="ResultNotReadyException">There is no extraction yet, or every attempt failed.</exception>
    public async Task<DocumentClassificationDiagnostics> GetClassificationDiagnosticsAsync(
        Guid id,
        CancellationToken ct)
    {
        var text = await GetTextAsync(id, ct).ConfigureAwait(false);

        var pages = JsonSerializer.Deserialize<List<StoredPage>>(text.Text.PageTextsJson, StoredJson) ?? [];
        var diagnostics = classifier.Diagnose(string.Join('\n', pages.Select(page => page.Text)));

        return new DocumentClassificationDiagnostics(
            text.Document,
            text.Text.Summary,
            [.. pages.Select(page => new DocumentTextPage(page.PageNumber, page.Text))],
            diagnostics);
    }

    /// <summary>
    /// Explains the extraction of the latest extraction: the OCR blocks with coordinates as the extractor received
    /// them and, for every field, what the rules in force now read, why a field failed and what its rule searched.
    /// Nothing is written.
    /// </summary>
    /// <exception cref="ResultNotReadyException">There is no extraction yet, or every attempt failed.</exception>
    public async Task<DocumentExtractionDiagnostics> GetExtractionDiagnosticsAsync(Guid id, CancellationToken ct)
    {
        var document = await GetByIdAsync(id, includeEvents: false, ct).ConfigureAwait(false);
        var stored = await repository.FindLatestExtractionOcrAsync(id, ct).ConfigureAwait(false)
            ?? throw new ResultNotReadyException(id, document.Status);
        var recorded = await repository.FindLatestExtractionResultAsync(id, ct).ConfigureAwait(false);

        var (ocr, source) = StoredOcr.Rebuild(
            stored.RawOcrResultJson,
            stored.PageTextsJson,
            stored.Summary.OcrProvider,
            stored.Summary.OcrModelVersion);
        var lines = OcrTextLine.From(ocr);

        var (documentType, typeSource) = ResolveDocumentType(document, lines);
        var extractor = documentType is null ? null : extractors.FirstOrDefault(candidate => candidate.DocumentType == documentType);

        var note = source == StoredOcr.PageText
            ? "This extraction was stored before OCR blocks were kept, so there are no coordinates and the rules ran on plain lines. Reprocess the document to get the real diagnosis."
            : null;

        if (extractor is null)
        {
            var reason = documentType is null
                ? "The document is UNKNOWN, so no extractor applies. See classification-diagnostics."
                : $"There is no extractor for {documentType}.";

            return new DocumentExtractionDiagnostics(
                document,
                stored.Summary,
                ExtractionDiagnoser.WithoutExtractor(documentType, typeSource, source, lines, note is null ? reason : $"{reason} {note}"));
        }

        var trace = new ExtractionTrace();
        var extraction = await extractor.ExtractAsync(ocr, trace, ct).ConfigureAwait(false);
        var recordedStatuses = recorded?.Fields.ToDictionary(field => field.FieldPath, field => field.ValidationStatus, StringComparer.Ordinal)
            ?? [];

        return new DocumentExtractionDiagnostics(
            document,
            stored.Summary,
            ExtractionDiagnoser.Build(
                documentType!, typeSource, extractor.Version, source, lines, extraction, trace, recordedStatuses, note));
    }

    private (string? Type, string Source) ResolveDocumentType(Document document, IReadOnlyList<OcrTextLine> lines)
    {
        if (!string.IsNullOrEmpty(document.DetectedDocumentType)
            && document.DetectedDocumentType != ClassificationResult.UnknownType)
        {
            return (document.DetectedDocumentType, "RECORDED");
        }

        var current = classifier.Diagnose(string.Join('\n', lines.Select(line => line.Text)));

        return current.DocumentType == ClassificationResult.UnknownType
            ? (null, "RECORDED")
            : (current.DocumentType, "CURRENT_CLASSIFICATION");
    }

    /// <summary>
    /// Opens the original bytes. The metadata is authoritative for the content type, so a renamed
    /// or mislabeled upload is still served as what it really is.
    /// </summary>
    public async Task<DocumentContentResult> OpenContentAsync(Guid id, CancellationToken ct)
    {
        var document = await GetByIdAsync(id, includeEvents: false, ct).ConfigureAwait(false);

        if (document.Status == DocumentStatus.Purged)
        {
            throw new DocumentPurgedException(id);
        }

        Stream content;
        try
        {
            content = await storage.OpenReadAsync(document.StorageRepositoryId, document.StorageKey, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            logger.LogError(
                "Stored blob is missing for an existing document. documentId={DocumentId} storageKey={StorageKey}",
                document.Id,
                document.StorageKey);
            throw new DocumentContentMissingException(document.Id);
        }

        return new DocumentContentResult(
            content,
            document.MimeType,
            document.OriginalFileName,
            document.SizeBytes,
            document.Sha256);
    }

    private sealed record StoredPage(int PageNumber, string Text);
}
