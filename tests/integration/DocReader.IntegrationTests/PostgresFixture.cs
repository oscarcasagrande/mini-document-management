using DocReader.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace DocReader.IntegrationTests;

/// <summary>
/// Banco real para os testes de integração.
///
/// A suíte aponta para o PostgreSQL do compose e cria um banco descartável por execução, para não
/// encostar nos dados de demonstração. Quando não há banco alcançável, os testes são pulados em vez
/// de falhar: <c>dotnet test</c> precisa continuar verde numa máquina sem o compose de pé.
///
///   docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d postgres
///   bash scripts/dotnet.sh test tests/integration/DocReader.IntegrationTests
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string EnvironmentVariable = "DOCREADER_TEST_CONNECTION";

    private static readonly string AdminConnectionString =
        Environment.GetEnvironmentVariable(EnvironmentVariable)
        ?? "Host=localhost;Port=5432;Database=postgres;Username=docreader;Password=docreader;Timeout=3";

    private string _databaseName = string.Empty;

    /// <summary>Null quando não há banco alcançável; os testes se pulam nesse caso.</summary>
    public string? ConnectionString { get; private set; }

    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        _databaseName = $"docreader_it_{Guid.NewGuid():N}";

        try
        {
            await using var admin = new NpgsqlConnection(AdminConnectionString);
            await admin.OpenAsync();

            await using (var create = admin.CreateCommand())
            {
                // O nome é gerado aqui, não vem de entrada externa, mas ainda assim vai citado.
                create.CommandText = $"CREATE DATABASE \"{_databaseName}\";";
                await create.ExecuteNonQueryAsync();
            }

            var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = _databaseName };
            ConnectionString = builder.ConnectionString;

            await using var context = CreateContext();
            await context.Database.MigrateAsync();
        }
        catch (Exception exception)
        {
            SkipReason =
                $"PostgreSQL não alcançável em {new NpgsqlConnectionStringBuilder(AdminConnectionString).Host}. " +
                $"Suba com docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d postgres. " +
                $"Detalhe: {exception.GetType().Name}";
            ConnectionString = null;
        }
    }

    public DocReaderDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DocReaderDbContext>()
            .UseNpgsql(ConnectionString ?? throw new InvalidOperationException("Sem banco de teste."))
            .Options;

        return new DocReaderDbContext(options);
    }

    public async ValueTask DisposeAsync()
    {
        if (ConnectionString is null)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();

        try
        {
            await using var admin = new NpgsqlConnection(AdminConnectionString);
            await admin.OpenAsync();

            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE);";
            await drop.ExecuteNonQueryAsync();
        }
        catch
        {
            // Banco descartável de teste: deixar para trás não justifica falhar a suíte.
        }
    }
}

[CollectionDefinition(nameof(PostgresCollection))]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
