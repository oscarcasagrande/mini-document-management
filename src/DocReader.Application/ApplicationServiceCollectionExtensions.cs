using DocReader.Application.Abstractions;
using DocReader.Application.Catalog;
using DocReader.Application.Classification;
using DocReader.Application.Documents;
using DocReader.Application.Extraction;
using DocReader.Application.Options;
using DocReader.Application.Processing;
using DocReader.Application.Retention;
using DocReader.Application.Storage;
using DocReader.Application.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocReader.Application;

/// <summary>
/// Registers the use cases and their options. Infrastructure bindings live in
/// <c>DocReader.Infrastructure</c>.
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddDocReaderApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<UploadOptions>()
            .Bind(configuration.GetSection(UploadOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<IdempotencyOptions>()
            .Bind(configuration.GetSection(IdempotencyOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<ProcessingQueueOptions>()
            .Bind(configuration.GetSection(ProcessingQueueOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<PagingOptions>()
            .Bind(configuration.GetSection(PagingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<SecretsOptions>()
            .Bind(configuration.GetSection(SecretsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SecretsOptions>, SecretsOptionsValidator>();

        services.AddOptions<WebhookOptions>()
            .Bind(configuration.GetSection(WebhookOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<PurgeOptions>()
            .Bind(configuration.GetSection(PurgeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ClassificationOptions>()
            .Bind(configuration.GetSection(ClassificationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddTimeProvider();

        services.AddSingleton<IDocumentClassifier, RulesDocumentClassifier>();

        services.AddScoped<IDocumentExtractor, BrCpfCardExtractor>();
        services.AddScoped<IDocumentExtractor, BrCinExtractor>();
        services.AddScoped<IDocumentExtractor, BrCnhExtractor>();
        services.AddScoped<IDocumentExtractor, BrProofOfAddressExtractor>();
        services.AddScoped<IDocumentExtractor, BrCnpjCardExtractor>();
        services.AddScoped<IDocumentExtractor, BrCcmeiExtractor>();
        services.AddScoped<IDocumentExtractor, BrSocialContractExtractor>();

        services.AddScoped<ProductServiceService>();
        services.AddScoped<RetentionService>();
        services.AddScoped<StorageRepositoryService>();
        services.AddScoped<WebhookSubscriptionService>();
        services.AddScoped<WebhookDispatchService>();
        services.AddScoped<StorageRepositoryResolver>();
        services.AddScoped<IFileStorage, RepositoryFileStorage>();
        services.AddScoped<RetentionPolicyService>();
        services.AddScoped<DocumentPurgeService>();
        services.AddScoped<DocumentUploadService>();
        services.AddScoped<DocumentQueryService>();
        services.AddScoped<DocumentDeletionService>();
        services.AddScoped<DocumentReprocessingService>();

        return services;
    }

    /// <summary>
    /// What only the worker needs: the pipeline and the option checks that keep healthy workers from
    /// taking each other's jobs.
    /// </summary>
    public static IServiceCollection AddDocReaderProcessing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<OcrProviderOptions>()
            .Bind(configuration.GetSection(OcrProviderOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<ProcessingQueueOptions>, ProcessingOptionsValidator>();

        services.AddScoped<DocumentProcessor>();

        return services;
    }

    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(TimeProvider)))
        {
            return;
        }

        services.AddSingleton(TimeProvider.System);
    }
}
