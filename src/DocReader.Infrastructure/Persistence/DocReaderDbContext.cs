using DocReader.Domain.Audit;
using DocReader.Domain.Backup;
using DocReader.Domain.Catalog;
using DocReader.Domain.Documents;
using DocReader.Domain.Extractions;
using DocReader.Domain.GdprDeletion;
using DocReader.Domain.Idempotency;
using DocReader.Domain.Processing;
using DocReader.Domain.Retention;
using DocReader.Domain.Storage;
using DocReader.Domain.StorageMigrations;
using DocReader.Domain.Webhooks;
using DocReader.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace DocReader.Infrastructure.Persistence;

/// <summary>
/// Single database context of the PoC. Schema changes always go through a migration; nothing here
/// ever calls EnsureCreated.
/// </summary>
public sealed class DocReaderDbContext(DbContextOptions<DocReaderDbContext> options) : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();

    public DbSet<ProductService> ProductServices => Set<ProductService>();

    public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();

    public DbSet<RetentionPolicy> RetentionPolicies => Set<RetentionPolicy>();

    public DbSet<RetentionReapplyRequest> RetentionReapplyRequests => Set<RetentionReapplyRequest>();

    public DbSet<GdprDeletionRequest> GdprDeletionRequests => Set<GdprDeletionRequest>();

    public DbSet<StorageRepository> StorageRepositories => Set<StorageRepository>();

    public DbSet<StorageMigrationJob> StorageMigrationJobs => Set<StorageMigrationJob>();

    public DbSet<BackupJob> BackupJobs => Set<BackupJob>();

    public DbSet<RestoreJob> RestoreJobs => Set<RestoreJob>();

    public DbSet<SystemState> SystemStates => Set<SystemState>();

    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();

    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    public DbSet<DocumentBlob> DocumentBlobs => Set<DocumentBlob>();

    public DbSet<DocumentEvent> DocumentEvents => Set<DocumentEvent>();

    public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();

    public DbSet<DocumentExtraction> Extractions => Set<DocumentExtraction>();

    public DbSet<ExtractedField> ExtractedFields => Set<ExtractedField>();

    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();

    public DbSet<ProtocolSequence> ProtocolSequences => Set<ProtocolSequence>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DocReaderDbContext).Assembly);
    }
}
