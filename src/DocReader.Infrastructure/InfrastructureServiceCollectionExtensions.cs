using DocReader.Application.Abstractions;
using DocReader.Application.Options;
using DocReader.Infrastructure.Files;
using DocReader.Infrastructure.Ocr;
using DocReader.Infrastructure.Persistence;
using DocReader.Infrastructure.Queue;
using DocReader.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddScoped<IIdempotencyStore, PostgresIdempotencyStore>();
        services.AddScoped<IProtocolGenerator, PostgresProtocolGenerator>();
        services.AddScoped<IProcessingQueue, PostgresProcessingQueue>();
        services.AddScoped<IDocumentProcessingStore, DocumentProcessingStore>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton<IPageCounter, DocumentPageCounter>();

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
