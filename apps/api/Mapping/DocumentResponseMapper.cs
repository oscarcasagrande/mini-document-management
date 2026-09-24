using DocReader.Api.Contracts.V1;
using DocReader.Application.Documents;
using DocReader.Domain.Documents;

namespace DocReader.Api.Mapping;

/// <summary>
/// Maps entities to the public contracts. Kept in one place so the API shape is reviewable without
/// reading every controller action.
/// </summary>
public static class DocumentResponseMapper
{
    public const string BasePath = "/api/v1/documents";

    public static DocumentLinks LinksFor(Guid id) => new(
        Self: $"{BasePath}/{id}",
        Status: $"{BasePath}/{id}/status",
        Content: $"{BasePath}/{id}/content",
        Download: $"{BasePath}/{id}/content?download=true");

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

    public static DocumentStatusResponse ToStatus(Document document) => new(
        document.Id,
        document.Protocol,
        document.Status,
        document.DetectedDocumentType,
        document.ClassificationConfidence,
        document.UploadedAt,
        document.CompletedAt,
        ToError(document));

    public static DocumentDetailResponse ToDetail(Document document) => new(
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
        // Extraction arrives with stage 2 of the execution plan; until then the original file and the
        // metadata are the whole answer.
        Extraction: null,
        ToError(document),
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

    public static PagedResponse<DocumentSummaryResponse> ToPagedResponse(PagedResult<Document> page) => new(
        page.Items.Select(ToSummary).ToArray(),
        page.Page,
        page.PageSize,
        page.TotalCount,
        page.TotalPages);

    private static DocumentClassificationResponse? ToClassification(Document document) =>
        document.DetectedDocumentType is null
            ? null
            : new DocumentClassificationResponse(document.DetectedDocumentType, document.ClassificationConfidence);

    private static DocumentErrorResponse? ToError(Document document) =>
        document.LastErrorCode is null
            ? null
            : new DocumentErrorResponse(document.LastErrorCode, document.LastErrorMessage ?? string.Empty);
}
