using DocReader.Domain.Documents;
using DocReader.Domain.Extractions;
using DocReader.Domain.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>extractions</c> table of PRD section 14. Reprocessing adds rows; nothing overwrites a
/// previous extraction. The document and job foreign keys cascade, so deleting a document (RF-014)
/// removes its results.
/// </summary>
public sealed class ExtractionConfiguration : IEntityTypeConfiguration<DocumentExtraction>
{
    public void Configure(EntityTypeBuilder<DocumentExtraction> builder)
    {
        builder.ToTable("extractions");

        builder.HasKey(extraction => extraction.Id);

        builder.Property(extraction => extraction.Id).HasColumnName("id");

        builder.Property(extraction => extraction.DocumentId)
            .HasColumnName("document_id")
            .IsRequired();

        builder.Property(extraction => extraction.ProcessingJobId)
            .HasColumnName("processing_job_id")
            .IsRequired();

        builder.Property(extraction => extraction.OcrProvider)
            .HasColumnName("ocr_provider")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(extraction => extraction.OcrModelVersion)
            .HasColumnName("ocr_model_version")
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(extraction => extraction.ClassifierVersion)
            .HasColumnName("classifier_version")
            .HasMaxLength(64);

        builder.Property(extraction => extraction.ExtractorVersion)
            .HasColumnName("extractor_version")
            .HasMaxLength(64);

        builder.Property(extraction => extraction.SchemaVersion)
            .HasColumnName("schema_version");

        builder.Property(extraction => extraction.RawText)
            .HasColumnName("raw_text")
            .IsRequired();

        builder.Property(extraction => extraction.PageTextsJson)
            .HasColumnName("page_texts")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(extraction => extraction.RawOcrResultJson)
            .HasColumnName("raw_ocr_result")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(extraction => extraction.StructuredResultJson)
            .HasColumnName("structured_result")
            .HasColumnType("jsonb");

        builder.Property(extraction => extraction.OverallConfidence)
            .HasColumnName("overall_confidence")
            .HasPrecision(5, 4);

        builder.Property(extraction => extraction.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(extraction => extraction.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ProcessingJob>()
            .WithMany()
            .HasForeignKey(extraction => extraction.ProcessingJobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(extraction => extraction.Fields)
            .WithOne()
            .HasForeignKey(field => field.ExtractionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(extraction => extraction.Fields)
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(extraction => new { extraction.DocumentId, extraction.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_extractions_document_id_created_at");

        builder.HasIndex(extraction => extraction.ProcessingJobId)
            .HasDatabaseName("ix_extractions_processing_job_id");
    }
}
