using DocReader.Domain.Storage;
using DocReader.Domain.StorageMigrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

public sealed class StorageMigrationJobConfiguration : IEntityTypeConfiguration<StorageMigrationJob>
{
    public void Configure(EntityTypeBuilder<StorageMigrationJob> builder)
    {
        builder.ToTable("storage_migration_jobs");

        builder.HasKey(job => job.Id);

        // The identity is assigned by the domain, so EF must insert it rather than guess an update.
        builder.Property(job => job.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(job => job.SourceRepositoryId)
            .HasColumnName("source_repository_id")
            .IsRequired();

        builder.Property(job => job.TargetRepositoryId)
            .HasColumnName("target_repository_id")
            .IsRequired();

        builder.Property(job => job.FilterDocumentType)
            .HasColumnName("filter_document_type")
            .HasMaxLength(64);

        builder.Property(job => job.FilterProductServiceId)
            .HasColumnName("filter_product_service_id");

        builder.Property(job => job.FilterUploadedFrom)
            .HasColumnName("filter_uploaded_from");

        builder.Property(job => job.FilterUploadedTo)
            .HasColumnName("filter_uploaded_to");

        builder.Property(job => job.Status)
            .HasColumnName("status")
            .HasConversion(new UpperSnakeCaseEnumConverter<StorageMigrationStatus>())
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(job => job.RequestedAt)
            .HasColumnName("requested_at")
            .IsRequired();

        builder.Property(job => job.StartedAt)
            .HasColumnName("started_at");

        builder.Property(job => job.CompletedAt)
            .HasColumnName("completed_at");

        builder.Property(job => job.DocumentsMigrated)
            .HasColumnName("documents_migrated")
            .IsRequired();

        builder.Property(job => job.DocumentsFailed)
            .HasColumnName("documents_failed")
            .IsRequired();

        builder.Property(job => job.ErrorMessage)
            .HasColumnName("error_message");

        builder.Ignore(job => job.Filter);
        builder.Ignore(job => job.CanCancel);

        // A repository with documents cannot be deleted, so these only cascade for a repository nothing is stored in.
        builder.HasOne<StorageRepository>()
            .WithMany()
            .HasForeignKey(job => job.SourceRepositoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<StorageRepository>()
            .WithMany()
            .HasForeignKey(job => job.TargetRepositoryId)
            .OnDelete(DeleteBehavior.Cascade);

        // No foreign key on the filter's product on purpose: it is a snapshot of the request, and a product deleted later
        // must make the filter match nothing, never widen it (as a SET NULL would) to every document of the source.

        builder.HasIndex(job => new { job.Status, job.RequestedAt })
            .HasDatabaseName("ix_storage_migration_jobs_status_requested_at");
    }
}
