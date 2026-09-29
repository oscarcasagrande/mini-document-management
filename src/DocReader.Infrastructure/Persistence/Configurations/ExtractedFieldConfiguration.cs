using DocReader.Application.Abstractions;
using DocReader.Domain.Extractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DocReader.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>extracted_fields</c> table of PRD section 14: one row per field of one extraction, with
/// the raw and the normalized value, the confidence and the evidence.
///
/// <see cref="ExtractedField.RawValue"/> and <see cref="ExtractedField.NormalizedValue"/> are the only PII-bearing
/// columns here (the bounding box and the validation messages are geometry/evidence, not the value itself), so
/// they go through a <see cref="ValueConverter{TModel,TProvider}"/> that calls <see cref="IFieldEncryptionProtector"/>:
/// encrypted at rest, transparent to every caller that reads <c>ExtractedField.RawValue</c> in memory. This
/// configuration therefore needs the protector at model-build time, so it is excluded from
/// <c>ApplyConfigurationsFromAssembly</c> (no public parameterless constructor) and applied explicitly by
/// <c>DocReaderDbContext.OnModelCreating</c>.
/// </summary>
public sealed class ExtractedFieldConfiguration(IFieldEncryptionProtector protector) : IEntityTypeConfiguration<ExtractedField>
{
    /// <summary>
    /// A JSON envelope is larger than its plaintext: base64 expands 3 bytes into 4 characters, a UTF-8 char in the
    /// BMP can take up to 3 bytes, and the envelope itself adds a nonce, a tag and ~50 characters of JSON keys and
    /// punctuation. Worst case for the previous 2048-character plaintext limit: 2048 chars × 3 bytes/char = 6144
    /// bytes, base64 of 6144 bytes = 8192 characters, plus nonce/tag/JSON overhead (well under 200 characters).
    /// 10000 keeps that with headroom instead of computing it to the character.
    /// </summary>
    private const int EncryptedColumnMaxLength = 10000;

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

        // Both converters share one delegate pair: null passes through untouched (nothing to encrypt for a field
        // that was NOT_FOUND), anything else is protected/unprotected with the field-encryption key. The value
        // comparer compares the MODEL side (the plaintext string), not the converted envelope: AES-GCM draws a
        // fresh random nonce on every Protect call, so the same plaintext produces a different envelope on every
        // save. Without an explicit comparer that snapshots the plaintext, change tracking could either miscompare
        // envelopes (spurious UPDATE on every SaveChanges for a value that did not change) or, if EF's default
        // happens to compare the pre-conversion string instead, work by accident; making the comparer explicit
        // means this is a decision, not a coincidence.
        var encryptedValueConverter = new ValueConverter<string?, string?>(
            plainText => plainText == null ? null : protector.Protect(plainText),
            storedText => storedText == null ? null : protector.Unprotect(storedText));

        var encryptedValueComparer = new ValueComparer<string?>(
            (left, right) => left == right,
            value => value == null ? 0 : value.GetHashCode(StringComparison.Ordinal),
            value => value);

        builder.Property(field => field.RawValue)
            .HasColumnName("raw_value")
            .HasConversion(encryptedValueConverter, encryptedValueComparer)
            .HasMaxLength(EncryptedColumnMaxLength);

        builder.Property(field => field.NormalizedValue)
            .HasColumnName("normalized_value")
            .HasConversion(encryptedValueConverter, encryptedValueComparer)
            .HasMaxLength(EncryptedColumnMaxLength);

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
