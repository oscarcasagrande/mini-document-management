namespace DocReader.Infrastructure.Storage;

/// <summary>The bytes of a document stored by the database provider. An infrastructure detail: the domain never sees it.</summary>
public sealed class DocumentBlob
{
    public Guid Id { get; init; }

    public Guid DocumentId { get; init; }

    public byte[] Content { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }
}
