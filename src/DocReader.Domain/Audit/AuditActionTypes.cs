namespace DocReader.Domain.Audit;

/// <summary>
/// Canonical action names written to <see cref="AuditLog"/>. Most are recorded by the generic
/// <c>AuditedAttribute</c> filter on the API's mutating endpoints; <see cref="StorageConfigRevealed"/> is
/// recorded by hand in <c>StorageRepositoryService.RevealConnectionConfigAsync</c>, the one call site that
/// existed before the generic filter did.
/// </summary>
public static class AuditActionTypes
{
    public const string StorageConfigRevealed = "STORAGE_CONFIG_REVEALED";

    public const string DocumentUploaded = "DOCUMENT_UPLOADED";
    public const string DocumentReprocessed = "DOCUMENT_REPROCESSED";
    public const string DocumentReclassificationTriggered = "DOCUMENT_RECLASSIFICATION_TRIGGERED";
    public const string DocumentDeleted = "DOCUMENT_DELETED";

    public const string RetentionPolicyCreated = "RETENTION_POLICY_CREATED";
    public const string RetentionPolicyUpdated = "RETENTION_POLICY_UPDATED";
    public const string RetentionPolicyReapplyTriggered = "RETENTION_POLICY_REAPPLY_TRIGGERED";
    public const string RetentionPolicyDeleted = "RETENTION_POLICY_DELETED";

    public const string StorageRepositoryCreated = "STORAGE_REPOSITORY_CREATED";
    public const string StorageRepositoryUpdated = "STORAGE_REPOSITORY_UPDATED";
    public const string StorageRepositoryDeleted = "STORAGE_REPOSITORY_DELETED";

    public const string ProductServiceCreated = "PRODUCT_SERVICE_CREATED";
    public const string ProductServiceUpdated = "PRODUCT_SERVICE_UPDATED";
    public const string ProductServiceDeleted = "PRODUCT_SERVICE_DELETED";

    public const string DocumentTypeCreated = "DOCUMENT_TYPE_CREATED";
    public const string DocumentTypeUpdated = "DOCUMENT_TYPE_UPDATED";
    public const string DocumentTypeDeleted = "DOCUMENT_TYPE_DELETED";

    public const string WebhookSubscriptionCreated = "WEBHOOK_SUBSCRIPTION_CREATED";
    public const string WebhookSubscriptionUpdated = "WEBHOOK_SUBSCRIPTION_UPDATED";
    public const string WebhookSubscriptionDeleted = "WEBHOOK_SUBSCRIPTION_DELETED";

    public const string BackupStarted = "BACKUP_STARTED";
    public const string RestoreStarted = "RESTORE_STARTED";

    public const string StorageMigrationStarted = "STORAGE_MIGRATION_STARTED";
    public const string StorageMigrationRolledBack = "STORAGE_MIGRATION_ROLLED_BACK";
}
