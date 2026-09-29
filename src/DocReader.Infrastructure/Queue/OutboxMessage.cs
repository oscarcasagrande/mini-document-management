using System.Text.Json;

namespace DocReader.Infrastructure.Queue;

/// <summary>
/// A transactional outbox row (ADR 0004). Written in the same transaction and <c>SaveChangesAsync</c>
/// call as the <c>processing_jobs</c> row it announces (<c>DocumentRepository.AcceptAsync</c> and its
/// reprocess counterpart), so a row exists if and only if the job does: no two-phase commit needed.
///
/// This lives in <c>DocReader.Infrastructure</c>, not <c>DocReader.Domain</c>: it is plumbing internal
/// to the RabbitMQ queue implementation, not a domain concept, and the RabbitMQ work is scoped to never
/// touch <c>Domain</c> or <c>Application</c>.
///
/// Only written when the configured provider is RabbitMQ; in PostgreSQL mode this table stays empty
/// forever.
/// </summary>
public sealed class OutboxMessage
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private OutboxMessage()
    {
    }

    public Guid Id { get; private init; }

    /// <summary>The document id, for inspection; the actual message content lives in <see cref="Payload"/>.</summary>
    public Guid AggregateId { get; private init; }

    /// <summary>JSON-serialized <see cref="OutboxJobPayload"/>: at minimum the job id and document id.</summary>
    public string Payload { get; private init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public int Attempts { get; private set; }

    public static OutboxMessage CreateForJob(Guid jobId, Guid documentId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            AggregateId = documentId,
            Payload = JsonSerializer.Serialize(new OutboxJobPayload(jobId, documentId), Json),
            CreatedAt = now
        };

    public OutboxJobPayload ReadPayload() =>
        JsonSerializer.Deserialize<OutboxJobPayload>(Payload, Json)
        ?? throw new InvalidOperationException($"Outbox message {Id} has an unreadable payload.");

    public void MarkPublished(DateTimeOffset now) => PublishedAt = now;

    public void RecordAttempt() => Attempts++;
}

/// <summary>Wire and storage shape of one queued job announcement: just enough to re-acquire it.</summary>
public sealed record OutboxJobPayload(Guid JobId, Guid DocumentId);
