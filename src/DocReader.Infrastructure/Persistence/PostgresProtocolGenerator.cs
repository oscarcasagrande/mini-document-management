using DocReader.Application.Abstractions;
using DocReader.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace DocReader.Infrastructure.Persistence;

/// <summary>
/// Allocates the daily protocol sequence with a single atomic upsert, so concurrent uploads never
/// receive the same protocol and no row level lock is held by the caller.
/// </summary>
public sealed class PostgresProtocolGenerator(DocReaderDbContext dbContext) : IProtocolGenerator
{
    private const string AllocateSql = """
        INSERT INTO protocol_sequences (day, last_number)
        VALUES (@day, 1)
        ON CONFLICT (day) DO UPDATE SET last_number = protocol_sequences.last_number + 1
        RETURNING last_number;
        """;

    public async Task<string> NextAsync(DateTimeOffset uploadedAt, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime(uploadedAt.UtcDateTime);

        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = AllocateSql;
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
            command.Parameters.Add(new NpgsqlParameter("day", NpgsqlDbType.Date) { Value = day });

            var raw = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var sequence = Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture);

            return DocumentProtocol.Format(day, sequence);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
