using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DocReader.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> when generating migrations. The connection string is never
/// contacted for that, so a placeholder is enough; override it with DOCREADER_DESIGN_CONNECTION
/// when a live database is needed.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DocReaderDbContext>
{
    private const string PlaceholderConnectionString =
        "Host=localhost;Port=5432;Database=docreader;Username=docreader;Password=design-time";

    public DocReaderDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("DOCREADER_DESIGN_CONNECTION")
            ?? PlaceholderConnectionString;

        var options = new DbContextOptionsBuilder<DocReaderDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
            .Options;

        return new DocReaderDbContext(options);
    }
}
