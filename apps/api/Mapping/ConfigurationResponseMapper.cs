using DocReader.Api.Contracts.V1;
using DocReader.Application.Documents;
using DocReader.Domain.Catalog;
using DocReader.Domain.Documents;
using DocReader.Domain.Retention;
using DocReader.Domain.Storage;
using DocReader.Domain.Webhooks;
using DocReader.Application.Webhooks;

namespace DocReader.Api.Mapping;

/// <summary>Maps the configuration resources (products, policies, repositories, webhooks) to their contracts.</summary>
public static class ConfigurationResponseMapper
{
    public static PagedResponse<TResponse> ToPage<TEntity, TResponse>(PagedResult<TEntity> page, Func<TEntity, TResponse> map)
    {
        return new PagedResponse<TResponse>([.. page.Items.Select(map)], page.Page, page.PageSize, page.TotalCount, page.TotalPages);
    }

    public static ProductServiceResponse ToResponse(ProductService productService) => new(
        productService.Id,
        productService.Code,
        productService.Name,
        productService.Active,
        ToReference(productService.StorageRepository),
        productService.CreatedAt,
        productService.UpdatedAt);

    public static WebhookSubscriptionResponse ToResponse(WebhookSubscription subscription) => new(
        subscription.Id,
        subscription.Url,
        subscription.Events,
        ToReference(subscription.ProductService),
        subscription.Active,
        subscription.CreatedAt,
        subscription.UpdatedAt);

    public static WebhookSubscriptionCreatedResponse ToCreatedResponse(CreatedWebhookSubscription created) => new(
        created.Subscription.Id,
        created.Subscription.Url,
        created.Subscription.Events,
        ToReference(created.Subscription.ProductService),
        created.Subscription.Active,
        created.Subscription.CreatedAt,
        created.Subscription.UpdatedAt,
        created.GeneratedSecret);

    public static WebhookDeliveryResponse ToResponse(WebhookDelivery delivery) => new(
        delivery.Id,
        delivery.DocumentId,
        delivery.Event,
        delivery.Status,
        delivery.AttemptCount,
        delivery.NextAttemptAt,
        delivery.LastAttemptAt,
        delivery.LastStatusCode,
        delivery.LastError,
        delivery.CreatedAt,
        delivery.CompletedAt);

    public static StorageRepositoryResponse ToResponse(StorageRepository repository) => new(
        repository.Id,
        repository.Code,
        repository.Name,
        repository.Provider,
        repository.IsDefault,
        repository.Active,
        repository.EncryptedConnectionConfig is not null,
        repository.IsImplemented,
        repository.CreatedAt,
        repository.UpdatedAt);

    public static StorageRepositoryReferenceResponse? ToReference(StorageRepository? repository) =>
        repository is null ? null : new StorageRepositoryReferenceResponse(repository.Id, repository.Code, repository.Name);

    public static RetentionPolicyResponse ToResponse(RetentionPolicy policy) => new(
        policy.Id,
        policy.DocumentType,
        ToReference(policy.ProductService),
        policy.RetentionDays,
        policy.Scope,
        policy.IsGlobal,
        policy.CreatedAt,
        policy.UpdatedAt);

    /// <summary>The purge date of a document and the policy behind it; null when the document never had one.</summary>
    public static DocumentRetentionResponse? ToRetention(Document document) =>
        document.ExpiresAt is null || document.RetentionDays is null
            ? null
            : new DocumentRetentionResponse(
                document.ExpiresAt.Value,
                document.RetentionDays.Value,
                document.RetentionPolicy is { } policy
                    ? new RetentionPolicyReferenceResponse(
                        policy.Id, policy.Scope, policy.DocumentType, policy.ProductService?.Code, policy.RetentionDays)
                    : null,
                document.PurgedAt);

    public static ProductServiceReferenceResponse? ToReference(ProductService? productService) =>
        productService is null ? null : new ProductServiceReferenceResponse(productService.Id, productService.Code, productService.Name);
}
