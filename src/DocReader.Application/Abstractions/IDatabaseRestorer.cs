namespace DocReader.Application.Abstractions;

/// <summary>Replays a database dump over the application database.</summary>
public interface IDatabaseRestorer
{
    /// <summary>
    /// Runs the dump in a single transaction that stops at the first error: either every statement applies, or
    /// PostgreSQL rolls the whole restore back and the database is exactly as it was before.
    /// </summary>
    Task RestoreAsync(string databaseDumpPath, CancellationToken ct);

    /// <summary>Over a fresh connection, how many of <paramref name="documentIds"/> have no row in <c>documents</c>.</summary>
    Task<int> CountMissingDocumentsAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken ct);
}
