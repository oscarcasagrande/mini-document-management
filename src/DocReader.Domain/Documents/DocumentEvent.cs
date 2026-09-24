namespace DocReader.Domain.Documents;

/// <summary>
/// Append-only timeline entry. Details never carry document content.
/// </summary>
public sealed class DocumentEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid DocumentId { get; init; }

    public string EventType { get; init; } = string.Empty;

    public DocumentStatus Stage { get; init; }

    public string? Details { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public static DocumentEvent Create(
        Guid documentId,
        string eventType,
        DocumentStatus stage,
        DateTimeOffset occurredAt,
        string? details = null) =>
        new()
        {
            DocumentId = documentId,
            EventType = eventType,
            Stage = stage,
            OccurredAt = occurredAt,
            Details = details
        };
}
