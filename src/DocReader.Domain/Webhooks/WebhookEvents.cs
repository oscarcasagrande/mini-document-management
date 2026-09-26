namespace DocReader.Domain.Webhooks;

/// <summary>The events a webhook subscription can ask for. The names are the ones receivers see.</summary>
public static class WebhookEvents
{
    public const string DocumentCompleted = "document.completed";
    public const string DocumentFailed = "document.failed";
    public const string DocumentPurged = "document.purged";

    public static IReadOnlyList<string> All { get; } = [DocumentCompleted, DocumentFailed, DocumentPurged];

    public static bool IsValid(string? name) => name is not null && All.Contains(name, StringComparer.Ordinal);
}
