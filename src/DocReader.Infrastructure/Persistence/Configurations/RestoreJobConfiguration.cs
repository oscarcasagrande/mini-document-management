using DocReader.Domain.Backup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>restore_jobs</c> is excluded from the dump (a restore must not replace the row of the job running it) and has no
/// foreign key into the tables the dump replaces. The migration adds a unique partial index that allows a single
/// pending or running job.
/// </summary>
public sealed class RestoreJobConfiguration : IEntityTypeConfiguration<RestoreJob>
{
    public void Configure(EntityTypeBuilder<RestoreJob> builder)
    {
        builder.ToTable("restore_jobs");

        builder.HasKey(job => job.Id);

        builder.Property(job => job.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(job => job.Status)
            .HasColumnName("status")
            .HasConversion(new UpperSnakeCaseEnumConverter<RestoreJobStatus>())
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(job => job.RequestedAt)
            .HasColumnName("requested_at")
            .IsRequired();

        builder.Property(job => job.StartedAt)
            .HasColumnName("started_at");

        builder.Property(job => job.CompletedAt)
            .HasColumnName("completed_at");

        builder.Property(job => job.ArchiveStorageRepositoryId)
            .HasColumnName("archive_storage_repository_id")
            .IsRequired();

        builder.Property(job => job.ArchiveStorageKey)
            .HasColumnName("archive_storage_key")
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(job => job.ArchiveSizeBytes)
            .HasColumnName("archive_size_bytes")
            .IsRequired();

        builder.Property(job => job.ArchiveChecksumSha256)
            .HasColumnName("archive_checksum_sha256")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(job => job.DocumentCount)
            .HasColumnName("document_count")
            .IsRequired();

        builder.Property(job => job.FilesRestored)
            .HasColumnName("files_restored");

        builder.Property(job => job.ErrorMessage)
            .HasColumnName("error_message")
            .HasMaxLength(RestoreJob.MaxErrorMessageLength);

        builder.HasIndex(job => new { job.Status, job.RequestedAt })
            .HasDatabaseName("ix_restore_jobs_status_requested_at");
    }
}
