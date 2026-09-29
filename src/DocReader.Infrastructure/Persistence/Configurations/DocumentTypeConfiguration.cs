using DocReader.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

public sealed class DocumentTypeConfiguration : IEntityTypeConfiguration<DocumentType>
{
    public void Configure(EntityTypeBuilder<DocumentType> builder)
    {
        builder.ToTable("document_types");

        builder.HasKey(documentType => documentType.Id);

        // The identity is assigned by the domain, so EF must insert it rather than guess an update.
        builder.Property(documentType => documentType.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(documentType => documentType.Code)
            .HasColumnName("code")
            .HasMaxLength(DocumentType.MaxCodeLength)
            .IsRequired();

        builder.Property(documentType => documentType.Name)
            .HasColumnName("name")
            .HasMaxLength(DocumentType.MaxNameLength)
            .IsRequired();

        builder.Property(documentType => documentType.SchemaJson)
            .HasColumnName("schema")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(documentType => documentType.ClassificationRulesJson)
            .HasColumnName("classification_rules")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(documentType => documentType.ExtractionRulesJson)
            .HasColumnName("extraction_rules")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(documentType => documentType.Active)
            .HasColumnName("active")
            .IsRequired();

        builder.Property(documentType => documentType.IsBuiltIn)
            .HasColumnName("is_built_in")
            .IsRequired();

        builder.Property(documentType => documentType.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(documentType => documentType.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(documentType => documentType.Code)
            .IsUnique()
            .HasDatabaseName("ux_document_types_code");

        // Ties, when scores are equal, favor the built-ins in the order code inherited from
        // DocumentTypeProfile.All, and any later custom type after them.
        builder.HasIndex(documentType => documentType.CreatedAt)
            .HasDatabaseName("ix_document_types_created_at");
    }
}
