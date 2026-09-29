using DocReader.Domain.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

public sealed class RetentionReapplyRequestConfiguration : IEntityTypeConfiguration<RetentionReapplyRequest>
{
    public void Configure(EntityTypeBuilder<RetentionReapplyRequest> builder)
    {
        builder.ToTable("retention_reapply_requests");

        builder.HasKey(request => request.Id);

        // The identity is assigned by the domain, so EF must insert it rather than guess an update.
        builder.Property(request => request.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(request => request.RetentionPolicyId)
            .HasColumnName("retention_policy_id")
            .IsRequired();

        builder.Property(request => request.Status)
            .HasColumnName("status")
            .HasConversion(new UpperSnakeCaseEnumConverter<RetentionReapplyStatus>())
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(request => request.RequestedAt)
            .HasColumnName("requested_at")
            .IsRequired();

        builder.Property(request => request.StartedAt)
            .HasColumnName("started_at");

        builder.Property(request => request.CompletedAt)
            .HasColumnName("completed_at");

        builder.Property(request => request.DocumentsUpdated)
            .HasColumnName("documents_updated")
            .IsRequired();

        builder.Property(request => request.ErrorMessage)
            .HasColumnName("error_message");

        builder.HasOne<RetentionPolicy>()
            .WithMany()
            .HasForeignKey(request => request.RetentionPolicyId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(request => new { request.Status, request.RequestedAt })
            .HasDatabaseName("ix_retention_reapply_requests_status_requested_at");
    }
}
