using System.Text.Json;
using DocReader.Api.Contracts.V1;
using DocReader.Application.Documents;
using DocReader.Domain.Documents;
using DocReader.Domain.Processing;

namespace DocReader.Api.Mapping;

/// <summary>
/// Maps entities to the public contracts. Kept in one place so the API shape is reviewable without
/// reading every controller action.
/// </summary>
public static class DocumentResponseMapper
{
    public const string BasePath = "/api/v1/documents";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static DocumentLinks LinksFor(Guid id) => new(
        Self: $"{BasePath}/{id}",
        Status: $"{BasePath}/{id}/status",
        Content: $"{BasePath}/{id}/content",
        Download: $"{BasePath}/{id}/content?download=true",
        Text: $"{BasePath}/{id}/text",
        Result: $"{BasePath}/{id}/result",
        Reprocess: $"{BasePath}/{id}/reprocess");

    public static UploadAcceptedResponse ToAcceptedResponse(UploadDocumentResult result)
    {
        var links = LinksFor(result.DocumentId);

        return new UploadAcceptedResponse(
            result.DocumentId,
            result.Protocol,
            result.Status,
            links.Status,
            links.Self,
            links.Content);
    }

    public static DocumentSummaryResponse ToSummary(Document document) => new(
        document.Id,
        document.Protocol,
        document.OriginalFileName,
        document.MimeType,
        document.SizeBytes,
        document.PageCount,
        document.UploadChannel,
        document.Status,
        document.ExpectedDocumentType,
        document.DetectedDocumentType,
        document.ClassificationConfidence,
        document.ExternalReference,
        document.UploadedAt,
        document.CompletedAt,
        LinksFor(document.Id));

    public static DocumentStatusResponse ToStatus(DocumentSnapshot snapshot, int maxAttempts)
    {
        var document = snapshot.Document;

        return new DocumentStatusResponse(
            document.Id,
            document.Protocol,
            document.Status,
            document.DetectedDocumentType,
            document.ClassificationConfidence,
            document.UploadedAt,
            document.CompletedAt,
            ToError(document),
            ToProcessing(snapshot.LatestJob, maxAttempts));
    }

    public static DocumentDetailResponse ToDetail(DocumentSnapshot snapshot, int maxAttempts)
    {
        var document = snapshot.Document;

        return new DocumentDetailResponse(
        document.Id,
        document.Protocol,
        document.Status,
        new DocumentUploadResponse(
            document.OriginalFileName,
            document.UploadChannel,
            document.UploadedAt,
            document.CompletedAt,
            document.ExternalReference,
            document.ExpectedDocumentType,
            document.MimeType,
            document.SizeBytes,
            document.PageCount,
            document.Sha256),
        ToClassification(document),
        ToExtraction(snapshot.LatestExtraction),
        ToError(document),
        ToProcessing(snapshot.LatestJob, maxAttempts),
        // The three acceptance events share one timestamp because they belong to a single
        // transaction, so the stage breaks the tie and keeps the timeline in lifecycle order.
        document.Events
            .OrderBy(documentEvent => documentEvent.OccurredAt)
            .ThenBy(documentEvent => documentEvent.Stage)
            .Select(documentEvent => new DocumentTimelineEntryResponse(
                documentEvent.EventType,
                documentEvent.Stage,
                documentEvent.Details,
                documentEvent.OccurredAt))
            .ToArray(),
        LinksFor(document.Id));
    }

    public static DocumentTextResponse ToText(DocumentText text)
    {
        var pages = JsonSerializer.Deserialize<List<TextPage>>(text.Text.PageTextsJson, Json) ?? [];

        return new DocumentTextResponse(
            text.Document.Id,
            text.Document.Protocol,
            text.Document.Status,
            text.Text.Summary.OcrProvider,
            text.Text.Summary.OcrModelVersion,
            text.Text.Summary.CreatedAt,
            pages.Select(page => new DocumentTextPageResponse(page.PageNumber, page.Text)).ToArray());
    }

    public static DocumentResultResponse ToResult(DocumentResult result)
    {
        var document = result.Document;
        var summary = result.Result.Summary;

        var fields = result.Result.Fields.ToDictionary(
            field => field.FieldPath,
            field => new ExtractedFieldResponse(
                field.RawValue,
                field.NormalizedValue,
                field.Confidence,
                field.ValidationStatus,
                JsonSerializer.Deserialize<List<string>>(field.ValidationMessagesJson, Json) ?? [],
                new FieldEvidenceResponse(
                    field.PageNumber,
                    JsonSerializer.Deserialize<List<decimal>>(field.BoundingBoxJson, Json) ?? [])),
            StringComparer.Ordinal);

        return new DocumentResultResponse(
            document.Id,
            document.Protocol,
            document.Status,
            new DocumentResultUploadResponse(
                document.OriginalFileName,
                document.UploadChannel,
                document.UploadedAt,
                document.ExternalReference),
            new DocumentResultClassificationResponse(
                document.DetectedDocumentType ?? "UNKNOWN",
                document.ClassificationConfidence,
                summary.ClassifierVersion),
            new DocumentResultExtractionResponse(
                summary.OcrProvider,
                summary.OcrModelVersion,
                summary.ExtractorVersion,
                summary.SchemaVersion,
                summary.OverallConfidence,
                summary.CreatedAt,
                fields));
    }

    public static PagedResponse<DocumentSummaryResponse> ToPagedResponse(PagedResult<Document> page) => new(
        page.Items.Select(ToSummary).ToArray(),
        page.Page,
        page.PageSize,
        page.TotalCount,
        page.TotalPages);

    private static DocumentExtractionResponse? ToExtraction(ExtractionSummary? summary) =>
        summary is null
            ? null
            : new DocumentExtractionResponse(
                summary.OcrProvider,
                summary.OcrModelVersion,
                summary.SchemaVersion,
                summary.OverallConfidence,
                summary.CreatedAt);

    private static DocumentProcessingResponse? ToProcessing(ProcessingJob? job, int maxAttempts) =>
        job is null
            ? null
            : new DocumentProcessingResponse(
                job.Status,
                job.AttemptCount,
                maxAttempts,
                job.PagesCompleted,
                job.PageCount,
                // A pending job that already ran once is waiting for its retry; the instant is when it may start.
                job is { Status: ProcessingJobStatus.Pending, AttemptCount: > 0 } ? job.AvailableAt : null);

    private sealed record TextPage(int PageNumber, string Text);

    private static DocumentClassificationResponse? ToClassification(Document document) =>
        document.DetectedDocumentType is null
            ? null
            : new DocumentClassificationResponse(document.DetectedDocumentType, document.ClassificationConfidence);

    private static DocumentErrorResponse? ToError(Document document) =>
        document.LastErrorCode is null
            ? null
            : new DocumentErrorResponse(document.LastErrorCode, document.LastErrorMessage ?? string.Empty);
}
