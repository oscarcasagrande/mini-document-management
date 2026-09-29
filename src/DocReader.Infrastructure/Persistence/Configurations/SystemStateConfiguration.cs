using DocReader.Domain.Backup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

/// <summary>One row, seeded by the migration. Excluded from the dump: a restore must not lower the gate it raised.</summary>
public sealed class SystemStateConfiguration : IEntityTypeConfiguration<SystemState>
{
    public void Configure(EntityTypeBuilder<SystemState> builder)
    {
        builder.ToTable("system_state");

        builder.HasKey(state => state.Id);

        builder.Property(state => state.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(state => state.IsReadOnly)
            .HasColumnName("is_read_only")
            .IsRequired();

        builder.Property(state => state.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();
    }
}
