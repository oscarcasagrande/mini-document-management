using DocReader.Domain.Documents;
using DocReader.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

public sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("documents");

        builder.HasKey(document => document.Id);

        builder.Property(document => document.Id)
            .HasColumnName("id");

        builder.Property(document => document.Protocol)
            .HasColumnName("protocol")
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(document => document.OriginalFileName)
            .HasColumnName("original_file_name")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(document => document.StorageKey)
            .HasColumnName("storage_key")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(document => document.StorageRepositoryId)
            .HasColumnName("storage_repository_id")
            .IsRequired();

        builder.HasOne<StorageRepository>()
            .WithMany()
            .HasForeignKey(document => document.StorageRepositoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(document => document.StorageRepositoryId)
            .HasDatabaseName("ix_documents_storage_repository_id");

        builder.Property(document => document.MimeType)
            .HasColumnName("mime_type")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(document => document.SizeBytes)
            .HasColumnName("size_bytes")
            .IsRequired();

        builder.Property(document => document.Sha256)
            .HasColumnName("sha256")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(document => document.PageCount)
            .HasColumnName("page_count")
            .IsRequired();

        builder.Property(document => document.UploadChannel)
            .HasColumnName("upload_channel")
            .HasConversion(new UpperSnakeCaseEnumConverter<UploadChannel>())
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(document => document.ExternalReference)
            .HasColumnName("external_reference")
            .HasMaxLength(256);

        builder.HasIndex(document => new { document.ExternalReference, document.UploadedAt })
            .HasDatabaseName("ix_documents_external_reference_uploaded_at")
            .HasFilter("external_reference IS NOT NULL");

        builder.Property(document => document.ExpectedDocumentType)
            .HasColumnName("expected_document_type")
            .HasMaxLength(64);

        builder.Property(document => document.ProductServiceId)
            .HasColumnName("product_service_id");

        builder.HasOne(document => document.ProductService)
            .WithMany()
            .HasForeignKey(document => document.ProductServiceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(document => document.DetectedDocumentType)
            .HasColumnName("detected_document_type")
            .HasMaxLength(64);

        builder.Property(document => document.ClassificationConfidence)
            .HasColumnName("classification_confidence")
            .HasPrecision(5, 4);

        builder.Property(document => document.Status)
            .HasColumnName("status")
            .HasConversion(new UpperSnakeCaseEnumConverter<DocumentStatus>())
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(document => document.UploadedAt)
            .HasColumnName("uploaded_at")
            .IsRequired();

        builder.Property(document => document.CompletedAt)
            .HasColumnName("completed_at");

        builder.Property(document => document.ExpiresAt)
            .HasColumnName("expires_at");

        builder.Property(document => document.RetentionPolicyId)
            .HasColumnName("retention_policy_id");

        builder.Property(document => document.RetentionDays)
            .HasColumnName("retention_days");

        builder.Property(document => document.PurgedAt)
            .HasColumnName("purged_at");

        // A policy can be deleted while documents still carry its deadline: they keep the date and the days.
        builder.HasOne(document => document.RetentionPolicy)
            .WithMany()
            .HasForeignKey(document => document.RetentionPolicyId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.Property(document => document.LastErrorCode)
            .HasColumnName("last_error_code")
            .HasMaxLength(64);

        builder.Property(document => document.LastErrorMessage)
            .HasColumnName("last_error_message")
            .HasMaxLength(2048);

        builder.HasIndex(document => document.Protocol)
            .IsUnique()
            .HasDatabaseName("ix_documents_protocol");

        builder.HasIndex(document => document.UploadedAt)
            .IsDescending()
            .HasDatabaseName("ix_documents_uploaded_at");

        builder.HasIndex(document => document.Status)
            .HasDatabaseName("ix_documents_status");

        builder.HasIndex(document => document.UploadChannel)
            .HasDatabaseName("ix_documents_upload_channel");

        builder.HasIndex(document => document.ProductServiceId)
            .HasDatabaseName("ix_documents_product_service_id");

        // What the purge job scans: it only cares about documents that still have a deadline to reach.
        builder.HasIndex(document => document.ExpiresAt)
            .HasFilter("status <> 'PURGED' AND expires_at IS NOT NULL")
            .HasDatabaseName("ix_documents_expires_at_pending");

        builder.HasIndex(document => document.Sha256)
            .HasDatabaseName("ix_documents_sha256");

        builder.HasIndex(document => document.DetectedDocumentType)
            .HasDatabaseName("ix_documents_detected_document_type");

        builder.HasMany(document => document.Events)
            .WithOne()
            .HasForeignKey(documentEvent => documentEvent.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(document => document.Events)
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .AutoInclude(false);
    }
}
