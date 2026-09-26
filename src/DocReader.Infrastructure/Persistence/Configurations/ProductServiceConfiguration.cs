using DocReader.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

public sealed class ProductServiceConfiguration : IEntityTypeConfiguration<ProductService>
{
    public void Configure(EntityTypeBuilder<ProductService> builder)
    {
        builder.ToTable("product_services");

        builder.HasKey(productService => productService.Id);

        // The identity is assigned by the domain, so EF must insert it rather than guess an update.
        builder.Property(productService => productService.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(productService => productService.Code)
            .HasColumnName("code")
            .HasMaxLength(ProductService.MaxCodeLength)
            .IsRequired();

        builder.Property(productService => productService.Name)
            .HasColumnName("name")
            .HasMaxLength(ProductService.MaxNameLength)
            .IsRequired();

        builder.Property(productService => productService.Active)
            .HasColumnName("active")
            .IsRequired();

        builder.Property(productService => productService.StorageRepositoryId)
            .HasColumnName("storage_repository_id");

        builder.HasOne(productService => productService.StorageRepository)
            .WithMany()
            .HasForeignKey(productService => productService.StorageRepositoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(productService => productService.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(productService => productService.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(productService => productService.Code)
            .IsUnique()
            .HasDatabaseName("ux_product_services_code");
    }
}
