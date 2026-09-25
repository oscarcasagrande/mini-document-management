using DocReader.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

public sealed class DocumentEventConfiguration : IEntityTypeConfiguration<DocumentEvent>
{
    public void Configure(EntityTypeBuilder<DocumentEvent> builder)
    {
        builder.ToTable("document_events");

        builder.HasKey(documentEvent => documentEvent.Id);

        // The domain assigns the key. Without this EF treats an event added to a tracked document as an
        // existing row and issues an UPDATE instead of an INSERT.
        builder.Property(documentEvent => documentEvent.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(documentEvent => documentEvent.DocumentId)
            .HasColumnName("document_id")
            .IsRequired();

        builder.Property(documentEvent => documentEvent.EventType)
            .HasColumnName("event_type")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(documentEvent => documentEvent.Stage)
            .HasColumnName("stage")
            .HasConversion(new UpperSnakeCaseEnumConverter<DocumentStatus>())
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(documentEvent => documentEvent.Details)
            .HasColumnName("details")
            .HasMaxLength(2048);

        builder.Property(documentEvent => documentEvent.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();

        builder.HasIndex(documentEvent => new { documentEvent.DocumentId, documentEvent.OccurredAt })
            .HasDatabaseName("ix_document_events_document_id_occurred_at");
    }
}
