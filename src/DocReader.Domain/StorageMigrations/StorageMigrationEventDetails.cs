using System.Globalization;

namespace DocReader.Domain.StorageMigrations;

/// <summary>
/// The details of a <see cref="Documents.DocumentEventTypes.StorageMigrated"/> timeline event:
/// <c>fromRepositoryId={guid} toRepositoryId={guid} migrationJobId={guid}</c>. The timeline is the only record of
/// where a document has lived, so this format is what the migration history of the API is read back from.
/// </summary>
public static class StorageMigrationEventDetails
{
    private const string FromKey = "fromRepositoryId";
    private const string ToKey = "toRepositoryId";
    private const string JobKey = "migrationJobId";

    public static string Format(Guid fromRepositoryId, Guid toRepositoryId, Guid? migrationJobId) =>
        migrationJobId is { } jobId
            ? string.Create(CultureInfo.InvariantCulture, $"{FromKey}={fromRepositoryId:D} {ToKey}={toRepositoryId:D} {JobMarker(jobId)}")
            : string.Create(CultureInfo.InvariantCulture, $"{FromKey}={fromRepositoryId:D} {ToKey}={toRepositoryId:D}");

    /// <summary>The fragment of the details that identifies the job that moved the document.</summary>
    public static string JobMarker(Guid migrationJobId) =>
        string.Create(CultureInfo.InvariantCulture, $"{JobKey}={migrationJobId:D}");

    public static bool TryParse(string? details, out Guid fromRepositoryId, out Guid toRepositoryId)
    {
        fromRepositoryId = Guid.Empty;
        toRepositoryId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(details))
        {
            return false;
        }

        Guid? from = null;
        Guid? to = null;

        foreach (var token in details.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || !Guid.TryParse(token.AsSpan(separator + 1), out var value))
            {
                continue;
            }

            var key = token[..separator];
            if (key == FromKey)
            {
                from = value;
            }
            else if (key == ToKey)
            {
                to = value;
            }
        }

        if (from is null || to is null)
        {
            return false;
        }

        fromRepositoryId = from.Value;
        toRepositoryId = to.Value;

        return true;
    }
}
