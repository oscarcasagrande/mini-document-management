using DocReader.Domain.Storage;
using DocReader.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

public sealed class StorageRepositoryConfiguration : IEntityTypeConfiguration<StorageRepository>
{
    public void Configure(EntityTypeBuilder<StorageRepository> builder)
    {
        builder.ToTable("storage_repositories");

        builder.HasKey(repository => repository.Id);

        builder.Property(repository => repository.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(repository => repository.Code)
            .HasColumnName("code")
            .HasMaxLength(StorageRepository.MaxCodeLength)
            .IsRequired();

        builder.Property(repository => repository.Name)
            .HasColumnName("name")
            .HasMaxLength(StorageRepository.MaxNameLength)
            .IsRequired();

        builder.Property(repository => repository.Provider)
            .HasColumnName("provider")
            .HasConversion(new UpperSnakeCaseEnumConverter<StorageProvider>())
            .HasMaxLength(32)
            .IsRequired();

        // An encrypted envelope, kept as jsonb: it is not queried, but the type documents what it holds.
        builder.Property(repository => repository.EncryptedConnectionConfig)
            .HasColumnName("connection_config")
            .HasColumnType("jsonb");

        builder.Property(repository => repository.IsDefault)
            .HasColumnName("is_default")
            .IsRequired();

        builder.Property(repository => repository.Active)
            .HasColumnName("active")
            .IsRequired();

        builder.Property(repository => repository.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(repository => repository.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.Ignore(repository => repository.IsImplemented);

        builder.HasIndex(repository => repository.Code)
            .IsUnique()
            .HasDatabaseName("ux_storage_repositories_code");

        // At most one row can be the default (the service keeps at least one).
        builder.HasIndex(repository => repository.IsDefault)
            .IsUnique()
            .HasFilter("is_default")
            .HasDatabaseName("ux_storage_repositories_default");
    }
}

public sealed class DocumentBlobConfiguration : IEntityTypeConfiguration<DocumentBlob>
{
    public void Configure(EntityTypeBuilder<DocumentBlob> builder)
    {
        builder.ToTable("document_blobs");

        builder.HasKey(blob => blob.Id);

        builder.Property(blob => blob.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(blob => blob.DocumentId)
            .HasColumnName("document_id")
            .IsRequired();

        builder.Property(blob => blob.Content)
            .HasColumnName("content")
            .HasColumnType("bytea")
            .IsRequired();

        builder.Property(blob => blob.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasIndex(blob => blob.DocumentId)
            .IsUnique()
            .HasDatabaseName("ux_document_blobs_document_id");
    }
}
