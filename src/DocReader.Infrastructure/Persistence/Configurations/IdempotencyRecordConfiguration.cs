using DocReader.Domain.Documents;
using DocReader.Domain.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_keys");

        builder.HasKey(record => record.Key);

        builder.Property(record => record.Key)
            .HasColumnName("key")
            .HasMaxLength(256);

        builder.Property(record => record.FileSha256)
            .HasColumnName("file_sha256")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(record => record.DocumentId)
            .HasColumnName("document_id")
            .IsRequired();

        builder.Property(record => record.ResponseStatus)
            .HasColumnName("response_status")
            .IsRequired();

        builder.Property(record => record.ResponseBody)
            .HasColumnName("response_body")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(record => record.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(record => record.ExpiresAt)
            .HasColumnName("expires_at")
            .IsRequired();

        // Deleting a document also frees its key, so a later upload with the same key is treated as
        // a new request instead of replaying a document that no longer exists.
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(record => record.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(record => record.ExpiresAt)
            .HasDatabaseName("ix_idempotency_keys_expires_at");
    }
}
