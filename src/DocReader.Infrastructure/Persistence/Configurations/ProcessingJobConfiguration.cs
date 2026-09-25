using DocReader.Domain.Documents;
using DocReader.Domain.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

/// <summary>
/// The queue table of ADR 0001. The composite index on status and availability is what keeps
/// <c>FOR UPDATE SKIP LOCKED</c> cheap.
/// </summary>
public sealed class ProcessingJobConfiguration : IEntityTypeConfiguration<ProcessingJob>
{
    public void Configure(EntityTypeBuilder<ProcessingJob> builder)
    {
        builder.ToTable("processing_jobs");

        builder.HasKey(job => job.Id);

        builder.Property(job => job.Id)
            .HasColumnName("id");

        builder.Property(job => job.DocumentId)
            .HasColumnName("document_id")
            .IsRequired();

        builder.Property(job => job.Status)
            .HasColumnName("status")
            .HasConversion(new UpperSnakeCaseEnumConverter<ProcessingJobStatus>())
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(job => job.Stage)
            .HasColumnName("stage")
            .HasConversion(new UpperSnakeCaseEnumConverter<DocumentStatus>())
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(job => job.AttemptCount)
            .HasColumnName("attempt_count")
            .IsRequired();

        builder.Property(job => job.AvailableAt)
            .HasColumnName("available_at")
            .IsRequired();

        builder.Property(job => job.LockedAt)
            .HasColumnName("locked_at");

        builder.Property(job => job.LockedBy)
            .HasColumnName("locked_by")
            .HasMaxLength(128);

        builder.Property(job => job.PagesCompleted)
            .HasColumnName("pages_completed")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(job => job.PageCount)
            .HasColumnName("page_count");

        builder.Property(job => job.StartedAt)
            .HasColumnName("started_at");

        builder.Property(job => job.FinishedAt)
            .HasColumnName("finished_at");

        builder.Property(job => job.ErrorCode)
            .HasColumnName("error_code")
            .HasMaxLength(64);

        builder.Property(job => job.ErrorMessage)
            .HasColumnName("error_message")
            .HasMaxLength(2048);

        builder.Property(job => job.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(job => job.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(job => new { job.Status, job.AvailableAt, job.CreatedAt })
            .HasDatabaseName("ix_processing_jobs_status_available_at_created_at");

        builder.HasIndex(job => job.DocumentId)
            .HasDatabaseName("ix_processing_jobs_document_id");

        // RF-013: never two jobs alive for the same document. The reprocessing path checks first,
        // and this index is what makes the rule hold even against a race.
        builder.HasIndex(job => job.DocumentId)
            .IsUnique()
            .HasFilter("status IN ('PENDING', 'RUNNING')")
            .HasDatabaseName("ux_processing_jobs_document_id_active");
    }
}
