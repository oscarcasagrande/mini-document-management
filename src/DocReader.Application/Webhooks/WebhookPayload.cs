using System.Globalization;
using System.Text.Json;
using DocReader.Domain;
using DocReader.Domain.Documents;
using DocReader.Domain.Webhooks;

namespace DocReader.Application.Webhooks;

/// <summary>
/// The body of a notification. It says which document reached which state, and nothing that was read from it: identifiers,
/// the status, the detected type, the product code and when. Built once when the notification is queued and stored as text, so
/// every retry sends the same bytes.
/// </summary>
public static class WebhookPayload
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The event a document reaching <paramref name="status"/> means, or null when the status is not one anyone is told about.</summary>
    public static string? EventFor(DocumentStatus status) => status switch
    {
        DocumentStatus.Completed => WebhookEvents.DocumentCompleted,
        DocumentStatus.Failed => WebhookEvents.DocumentFailed,
        DocumentStatus.Purged => WebhookEvents.DocumentPurged,
        _ => null
    };

    public static string Build(string webhookEvent, Document document, string? productServiceCode, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(document);

        return JsonSerializer.Serialize(
            new Body(
                webhookEvent,
                document.Id,
                document.Protocol,
                EnumNaming.ToUpperSnakeCase(document.Status),
                document.DetectedDocumentType,
                productServiceCode,
                occurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)),
            Json);
    }

    private sealed record Body(
        string Event,
        Guid DocumentId,
        string Protocol,
        string Status,
        string? DetectedDocumentType,
        string? ProductServiceCode,
        string OccurredAt);
}
