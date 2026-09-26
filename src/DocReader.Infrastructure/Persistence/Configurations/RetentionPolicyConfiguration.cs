using DocReader.Domain.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

public sealed class RetentionPolicyConfiguration : IEntityTypeConfiguration<RetentionPolicy>
{
    public void Configure(EntityTypeBuilder<RetentionPolicy> builder)
    {
        builder.ToTable("retention_policies", table =>
        {
            table.HasCheckConstraint("ck_retention_policies_days", "retention_days >= 1 AND retention_days <= 36500");
        });

        builder.HasKey(policy => policy.Id);

        builder.Property(policy => policy.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(policy => policy.DocumentType)
            .HasColumnName("document_type")
            .HasMaxLength(64);

        builder.Property(policy => policy.ProductServiceId)
            .HasColumnName("product_service_id");

        builder.Property(policy => policy.RetentionDays)
            .HasColumnName("retention_days")
            .IsRequired();

        builder.Property(policy => policy.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(policy => policy.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasOne(policy => policy.ProductService)
            .WithMany()
            .HasForeignKey(policy => policy.ProductServiceId)
            .OnDelete(DeleteBehavior.Restrict);

        // One policy per scope, and exactly one global (both null): NULLs count as equal in this index, so
        // (type, null), (null, product) and (null, null) cannot repeat either.
        builder.HasIndex(policy => new { policy.DocumentType, policy.ProductServiceId })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_retention_policies_scope");

        builder.Ignore(policy => policy.IsGlobal);
        builder.Ignore(policy => policy.Scope);
    }
}
