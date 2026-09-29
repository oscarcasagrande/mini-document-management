using DocReader.Application.Abstractions;
using DocReader.Application.Audit;
using DocReader.Application.Options;
using DocReader.Application.Webhooks;
using DocReader.Infrastructure.Backup;
using DocReader.Infrastructure.Encryption;
using DocReader.Infrastructure.Files;
using DocReader.Infrastructure.Ocr;
using DocReader.Infrastructure.Persistence;
using DocReader.Infrastructure.Queue;
using DocReader.Infrastructure.Queue.RabbitMq;
using DocReader.Infrastructure.Storage;
using DocReader.Infrastructure.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocReader.Infrastructure;

/// <summary>
/// Binds the application contracts to the PoC implementations: PostgreSQL and a local volume.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    public const string ConnectionStringName = "Default";

    public static IServiceCollection AddDocReaderInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string {ConnectionStringName} is not configured.");

        services.AddDbContext<DocReaderDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations_history");
                npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(5), null);
            });
        });

        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IProductServiceRepository, ProductServiceRepository>();
        services.AddScoped<IDocumentTypeRepository, DocumentTypeRepository>();
        services.AddScoped<IRetentionPolicyRepository, RetentionPolicyRepository>();
        services.AddScoped<IRetentionReapplyRequestRepository, RetentionReapplyRequestRepository>();
        services.AddScoped<IGdprDeletionRequestRepository, GdprDeletionRequestRepository>();
        services.AddScoped<IStorageMigrationJobRepository, StorageMigrationJobRepository>();
        services.AddScoped<IIdempotencyStore, PostgresIdempotencyStore>();
        services.AddScoped<IProtocolGenerator, PostgresProtocolGenerator>();

        // ADR 0001 stays the default: DocReader:Queue:Provider (QUEUE_PROVIDER) picked, once, straight
        // from configuration, the same way the connection string above is - not through an
        // IOptions<ProcessingQueueOptions> binding, because that class belongs to DocReader.Application
        // and this switch is Infrastructure-only wiring (ADR 0004).
        var queueProvider = QueueProviderOptions.FromConfigurationValue(
            configuration[QueueProviderOptions.ConfigurationKey]);
        services.AddSingleton(queueProvider);

        services.AddOptions<RabbitMqOptions>().Bind(configuration.GetSection(RabbitMqOptions.SectionName));

        if (queueProvider.Provider == QueueProvider.RabbitMq)
        {
            // Singleton: owns the one connection/consumer for the whole process. Registered here (not
            // hosted) so both api and worker can resolve IProcessingQueue without api ever starting a
            // broker connection - only worker's Program.cs adds it as an IHostedService.
            services.AddSingleton<RabbitMqJobBridge>();
            services.AddScoped<IProcessingQueue, RabbitMqProcessingQueue>();
        }
        else
        {
            services.AddScoped<IProcessingQueue, PostgresProcessingQueue>();
        }

        services.AddScoped<IDocumentProcessingStore, DocumentProcessingStore>();
        services.AddScoped<IWebhookSubscriptionRepository, WebhookSubscriptionRepository>();
        services.AddScoped<IWebhookDeliveryStore, WebhookDeliveryStore>();
        services.AddSingleton<IWebhookSender, HttpWebhookSender>();
        services.AddHttpClient(HttpWebhookSender.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                var webhooks = provider.GetRequiredService<IOptions<WebhookOptions>>();

                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    ConnectCallback = (context, ct) => HttpWebhookSender.ConnectGuardedAsync(context, webhooks.Value.AllowPrivateNetworks, ct)
                };
            });
        services.AddScoped<IAuditLogStore, EfAuditLogStore>();
        services.AddScoped<IStorageRepositoryStore, StorageRepositoryStore>();
        services.AddScoped<IStorageAdapterFactory, StorageAdapterFactory>();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.AddSingleton<IFieldEncryptionProtector, AesGcmFieldEncryptionProtector>();
        services.AddSingleton<IPageCounter, DocumentPageCounter>();

        services.AddScoped<IBackupJobRepository, BackupJobRepository>();
        services.AddScoped<IRestoreJobRepository, RestoreJobRepository>();
        services.AddScoped<ISystemStateStore, SystemStateStore>();
        services.AddSingleton<BackupSignature>();
        services.AddSingleton(provider => new PostgresBackupTool(
            connectionString,
            provider.GetRequiredService<IOptions<BackupOptions>>(),
            provider.GetRequiredService<ILogger<PostgresBackupTool>>()));
        services.AddSingleton<IDatabaseDumper>(provider => provider.GetRequiredService<PostgresBackupTool>());
        services.AddSingleton<IDatabaseRestorer>(provider => provider.GetRequiredService<PostgresBackupTool>());
        services.AddScoped<IBackupArchiveBuilder, TarGzBackupArchiveBuilder>();
        services.AddScoped<IBackupArchiveReader, TarGzBackupArchiveReader>();

        return services;
    }

    /// <summary>
    /// Starts the RabbitMQ connection/consumer/outbox publisher topology as a hosted service (ADR 0004).
    /// Only <c>apps/worker</c> calls this, and only guarded by <c>QueueProviderOptions.Provider ==
    /// QueueProvider.RabbitMq</c>: <c>apps/api</c> never consumes jobs, and calling this when
    /// <see cref="RabbitMqJobBridge"/> was never registered (Postgres mode) would throw at resolution
    /// time. The factory overload reuses the exact singleton instance
    /// <see cref="AddDocReaderInfrastructure"/> already registered, so DI consumers of
    /// <see cref="RabbitMqJobBridge"/> (namely <c>RabbitMqProcessingQueue</c>) and the hosted service
    /// that actually opens the connection are the same object - never two disconnected bridges.
    /// </summary>
    public static IServiceCollection AddDocReaderRabbitMqConsumer(this IServiceCollection services)
    {
        services.AddHostedService(provider => provider.GetRequiredService<RabbitMqJobBridge>());

        return services;
    }

    /// <summary>
    /// Binds <see cref="IDocumentOcrProvider"/> to the internal ocr-service. Only the worker calls the
    /// service to read pages; the API merely probes its health, with its own client.
    /// </summary>
    public static IServiceCollection AddDocReaderOcrProvider(this IServiceCollection services)
    {
        services.AddHttpClient<IDocumentOcrProvider, PaddleOcrServiceProvider>((provider, client) =>
        {
            var ocr = provider.GetRequiredService<IOptions<OcrProviderOptions>>().Value;

            client.BaseAddress = new Uri(ocr.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);

            // The budget of a page is enforced per call by the provider, so the client itself must
            // not cut a slow page short with its own default of 100 seconds.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        return services;
    }
}
