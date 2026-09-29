using DocReader.Domain.Backup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>backup_jobs</c> is operational state of this installation, not data a backup carries: it is excluded from the
/// dump, so a restore never rewrites it. For the same reason it has no foreign key to a table the dump does carry
/// (<c>DROP TABLE</c> of that table during a restore would otherwise fail on the dependency).
/// </summary>
public sealed class BackupJobConfiguration : IEntityTypeConfiguration<BackupJob>
{
    public void Configure(EntityTypeBuilder<BackupJob> builder)
    {
        builder.ToTable("backup_jobs");

        builder.HasKey(job => job.Id);

        builder.Property(job => job.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(job => job.Status)
            .HasColumnName("status")
            .HasConversion(new UpperSnakeCaseEnumConverter<BackupJobStatus>())
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(job => job.RequestedAt)
            .HasColumnName("requested_at")
            .IsRequired();

        builder.Property(job => job.StartedAt)
            .HasColumnName("started_at");

        builder.Property(job => job.CompletedAt)
            .HasColumnName("completed_at");

        builder.Property(job => job.StorageRepositoryId)
            .HasColumnName("storage_repository_id")
            .IsRequired();

        builder.Property(job => job.StorageKey)
            .HasColumnName("storage_key")
            .HasMaxLength(512);

        builder.Property(job => job.FileName)
            .HasColumnName("file_name")
            .HasMaxLength(128);

        builder.Property(job => job.SizeBytes)
            .HasColumnName("size_bytes");

        builder.Property(job => job.ChecksumSha256)
            .HasColumnName("checksum_sha256")
            .HasMaxLength(64);

        builder.Property(job => job.DocumentCount)
            .HasColumnName("document_count");

        builder.Property(job => job.FilesArchived)
            .HasColumnName("files_archived");

        builder.Property(job => job.ErrorMessage)
            .HasColumnName("error_message")
            .HasMaxLength(BackupJob.MaxErrorMessageLength);

        builder.HasIndex(job => new { job.Status, job.RequestedAt })
            .HasDatabaseName("ix_backup_jobs_status_requested_at");
    }
}
