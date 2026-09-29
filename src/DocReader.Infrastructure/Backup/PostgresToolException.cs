namespace DocReader.Infrastructure.Backup;

/// <summary><c>pg_dump</c> or <c>psql</c> could not be started, timed out or exited with an error.</summary>
public sealed class PostgresToolException(string message, Exception? innerException = null) : Exception(message, innerException);
