using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

public sealed class ProtocolSequenceConfiguration : IEntityTypeConfiguration<ProtocolSequence>
{
    public void Configure(EntityTypeBuilder<ProtocolSequence> builder)
    {
        builder.ToTable("protocol_sequences");

        builder.HasKey(sequence => sequence.Day);

        builder.Property(sequence => sequence.Day)
            .HasColumnName("day")
            .HasColumnType("date");

        builder.Property(sequence => sequence.LastNumber)
            .HasColumnName("last_number")
            .IsRequired();
    }
}
