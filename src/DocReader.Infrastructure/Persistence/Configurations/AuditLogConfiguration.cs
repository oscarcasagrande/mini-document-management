using DocReader.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");

        builder.HasKey(entry => entry.Id);

        // The domain assigns the key. Without this EF treats a new entry as an existing row and issues an
        // UPDATE (0 rows affected) instead of an INSERT.
        builder.Property(entry => entry.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(entry => entry.UserId)
            .HasColumnName("user_id")
            .HasMaxLength(256);

        builder.Property(entry => entry.Action)
            .HasColumnName("action")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entry => entry.ResourceType)
            .HasColumnName("resource_type")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(entry => entry.ResourceId)
            .HasColumnName("resource_id")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(entry => entry.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();

        builder.Property(entry => entry.IpAddress)
            .HasColumnName("ip_address")
            .HasMaxLength(64);

        builder.Property(entry => entry.UserAgent)
            .HasColumnName("user_agent")
            .HasMaxLength(512);

        builder.Property(entry => entry.Changes)
            .HasColumnName("changes")
            .HasMaxLength(AuditLog.MaxChangesLength);

        builder.HasIndex(entry => new { entry.ResourceType, entry.ResourceId, entry.OccurredAt })
            .HasDatabaseName("ix_audit_logs_resource_type_resource_id_occurred_at");

        builder.HasIndex(entry => entry.OccurredAt)
            .HasDatabaseName("ix_audit_logs_occurred_at");
    }
}
