using DocReader.Application.Abstractions;
using DocReader.Infrastructure.Files;
using DocReader.Infrastructure.Persistence;
using DocReader.Infrastructure.Queue;
using DocReader.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton<IPageCounter, DocumentPageCounter>();

        return services;
    }
}
