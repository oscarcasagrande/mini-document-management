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

        builder.Property(documentEvent => documentEvent.Id)
            .HasColumnName("id");

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
