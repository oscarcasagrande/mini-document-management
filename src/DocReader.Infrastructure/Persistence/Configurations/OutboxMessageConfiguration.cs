using DocReader.Infrastructure.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(message => message.Id);

        // Guid assigned by the entity's own factory method, not by the database: without this EF would
        // treat a newly added row as an existing one and emit a zero-row UPDATE instead of an INSERT.
        builder.Property(message => message.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(message => message.AggregateId)
            .HasColumnName("aggregate_id")
            .IsRequired();

        builder.Property(message => message.Payload)
            .HasColumnName("payload")
            .IsRequired();

        builder.Property(message => message.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(message => message.PublishedAt)
            .HasColumnName("published_at");

        builder.Property(message => message.Attempts)
            .HasColumnName("attempts")
            .IsRequired();

        // What RabbitMqOutboxPublisher scans: only unpublished rows, oldest first.
        builder.HasIndex(message => message.CreatedAt)
            .HasFilter("published_at IS NULL")
            .HasDatabaseName("ix_outbox_messages_unpublished");
    }
}
