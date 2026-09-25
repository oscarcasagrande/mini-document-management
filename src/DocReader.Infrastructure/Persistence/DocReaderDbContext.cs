using DocReader.Domain.Documents;
using DocReader.Domain.Extractions;
using DocReader.Domain.Idempotency;
using DocReader.Domain.Processing;
using Microsoft.EntityFrameworkCore;

namespace DocReader.Infrastructure.Persistence;

/// <summary>
/// Single database context of the PoC. Schema changes always go through a migration; nothing here
/// ever calls EnsureCreated.
/// </summary>
public sealed class DocReaderDbContext(DbContextOptions<DocReaderDbContext> options) : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentEvent> DocumentEvents => Set<DocumentEvent>();

    public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();

    public DbSet<DocumentExtraction> Extractions => Set<DocumentExtraction>();

    public DbSet<ExtractedField> ExtractedFields => Set<ExtractedField>();

    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();

    public DbSet<ProtocolSequence> ProtocolSequences => Set<ProtocolSequence>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DocReaderDbContext).Assembly);
    }
}
