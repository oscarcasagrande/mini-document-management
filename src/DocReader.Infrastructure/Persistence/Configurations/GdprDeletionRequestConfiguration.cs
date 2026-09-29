using DocReader.Domain.Documents;
using DocReader.Domain.GdprDeletion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

public sealed class GdprDeletionRequestConfiguration : IEntityTypeConfiguration<GdprDeletionRequest>
{
    public void Configure(EntityTypeBuilder<GdprDeletionRequest> builder)
    {
        builder.ToTable("gdpr_deletion_requests");

        builder.HasKey(request => request.Id);

        // The identity is assigned by the domain, so EF must insert it rather than guess an update.
        builder.Property(request => request.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(request => request.DocumentId)
            .HasColumnName("document_id")
            .IsRequired();

        builder.Property(request => request.RequestedBy)
            .HasColumnName("requested_by")
            .HasMaxLength(256);

        builder.Property(request => request.RequestedAt)
            .HasColumnName("requested_at")
            .IsRequired();

        builder.Property(request => request.Reason)
            .HasColumnName("reason")
            .HasMaxLength(512);

        builder.Property(request => request.Status)
            .HasColumnName("status")
            .HasConversion(new UpperSnakeCaseEnumConverter<GdprDeletionRequestStatus>())
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(request => request.ApprovedBy)
            .HasColumnName("approved_by")
            .HasMaxLength(256);

        builder.Property(request => request.DecidedAt)
            .HasColumnName("decided_at");

        builder.Property(request => request.ExecutedAt)
            .HasColumnName("executed_at");

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(request => request.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(request => new { request.Status, request.RequestedAt })
            .HasDatabaseName("ix_gdpr_deletion_requests_status_requested_at");

        builder.HasIndex(request => request.DocumentId)
            .HasDatabaseName("ix_gdpr_deletion_requests_document_id");
    }
}
