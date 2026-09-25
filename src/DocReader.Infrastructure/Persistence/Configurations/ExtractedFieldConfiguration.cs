using DocReader.Domain.Extractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>extracted_fields</c> table of PRD section 14: one row per field of one extraction, with
/// the raw and the normalized value, the confidence and the evidence.
/// </summary>
public sealed class ExtractedFieldConfiguration : IEntityTypeConfiguration<ExtractedField>
{
    public void Configure(EntityTypeBuilder<ExtractedField> builder)
    {
        builder.ToTable("extracted_fields");

        builder.HasKey(field => field.Id);

        builder.Property(field => field.Id).HasColumnName("id");

        builder.Property(field => field.ExtractionId)
            .HasColumnName("extraction_id")
            .IsRequired();

        builder.Property(field => field.FieldPath)
            .HasColumnName("field_path")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(field => field.RawValue)
            .HasColumnName("raw_value")
            .HasMaxLength(2048);

        builder.Property(field => field.NormalizedValue)
            .HasColumnName("normalized_value")
            .HasMaxLength(2048);

        builder.Property(field => field.Confidence)
            .HasColumnName("confidence")
            .HasPrecision(5, 4);

        builder.Property(field => field.PageNumber)
            .HasColumnName("page_number");

        builder.Property(field => field.BoundingBoxJson)
            .HasColumnName("bounding_box")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(field => field.ValidationStatus)
            .HasColumnName("validation_status")
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(field => field.ValidationMessagesJson)
            .HasColumnName("validation_messages")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.HasIndex(field => new { field.ExtractionId, field.FieldPath })
            .IsUnique()
            .HasDatabaseName("ix_extracted_fields_extraction_id_field_path");
    }
}
