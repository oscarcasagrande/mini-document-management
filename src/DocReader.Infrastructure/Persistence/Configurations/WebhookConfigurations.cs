using DocReader.Domain.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocReader.Infrastructure.Persistence.Configurations;

public sealed class WebhookSubscriptionConfiguration : IEntityTypeConfiguration<WebhookSubscription>
{
    public void Configure(EntityTypeBuilder<WebhookSubscription> builder)
    {
        builder.ToTable("webhook_subscriptions");

        builder.HasKey(subscription => subscription.Id);

        builder.Property(subscription => subscription.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(subscription => subscription.Url)
            .HasColumnName("url")
            .HasMaxLength(WebhookSubscription.MaxUrlLength)
            .IsRequired();

        // The signing secret, as the encrypted envelope (see ISecretProtector): never plain text in the database.
        builder.Property(subscription => subscription.EncryptedSecret)
            .HasColumnName("secret")
            .IsRequired();

        builder.Property(subscription => subscription.Events)
            .HasColumnName("events")
            .HasColumnType("text[]")
            .IsRequired();

        builder.Property(subscription => subscription.ProductServiceId)
            .HasColumnName("product_service_id");

        builder.HasOne(subscription => subscription.ProductService)
            .WithMany()
            .HasForeignKey(subscription => subscription.ProductServiceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(subscription => subscription.Active)
            .HasColumnName("active")
            .IsRequired();

        builder.Property(subscription => subscription.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(subscription => subscription.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(subscription => subscription.ProductServiceId)
            .HasDatabaseName("ix_webhook_subscriptions_product_service_id");
    }
}

public sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.ToTable("webhook_deliveries");

        builder.HasKey(delivery => delivery.Id);

        builder.Property(delivery => delivery.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(delivery => delivery.SubscriptionId)
            .HasColumnName("subscription_id")
            .IsRequired();

        builder.Property(delivery => delivery.DocumentId)
            .HasColumnName("document_id")
            .IsRequired();

        builder.Property(delivery => delivery.Event)
            .HasColumnName("event")
            .HasMaxLength(64)
            .IsRequired();

        // Text, not jsonb: what is signed and sent must be byte for byte what was built, and jsonb would reorder and reformat it.
        builder.Property(delivery => delivery.Payload)
            .HasColumnName("payload")
            .IsRequired();

        builder.Property(delivery => delivery.Status)
            .HasColumnName("status")
            .HasConversion(new UpperSnakeCaseEnumConverter<WebhookDeliveryStatus>())
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(delivery => delivery.AttemptCount)
            .HasColumnName("attempt_count")
            .IsRequired();

        builder.Property(delivery => delivery.NextAttemptAt)
            .HasColumnName("next_attempt_at")
            .IsRequired();

        builder.Property(delivery => delivery.LastAttemptAt)
            .HasColumnName("last_attempt_at");

        builder.Property(delivery => delivery.LastStatusCode)
            .HasColumnName("last_status_code");

        builder.Property(delivery => delivery.LastError)
            .HasColumnName("last_error")
            .HasMaxLength(64);

        builder.Property(delivery => delivery.LockedAt)
            .HasColumnName("locked_at");

        builder.Property(delivery => delivery.LockedBy)
            .HasColumnName("locked_by")
            .HasMaxLength(128);

        builder.Property(delivery => delivery.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(delivery => delivery.CompletedAt)
            .HasColumnName("completed_at");

        // Deleting a subscription drops what it still had queued; deleting a document drops the notifications about it.
        builder.HasOne(delivery => delivery.Subscription)
            .WithMany()
            .HasForeignKey(delivery => delivery.SubscriptionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<DocReader.Domain.Documents.Document>()
            .WithMany()
            .HasForeignKey(delivery => delivery.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        // What the dispatcher scans: only pending rows, by when they are due.
        builder.HasIndex(delivery => delivery.NextAttemptAt)
            .HasFilter("status = 'PENDING'")
            .HasDatabaseName("ix_webhook_deliveries_pending_due");

        builder.HasIndex(delivery => new { delivery.SubscriptionId, delivery.CreatedAt })
            .HasDatabaseName("ix_webhook_deliveries_subscription");

        builder.HasIndex(delivery => delivery.DocumentId)
            .HasDatabaseName("ix_webhook_deliveries_document");
    }
}
