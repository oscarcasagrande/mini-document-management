namespace DocReader.Application.Backup;

/// <summary>The database was restored, but some archived document files could not be written back.</summary>
public sealed class FilesNotRestoredException(int restored, IReadOnlyList<Guid> failedDocumentIds)
    : Exception(BuildMessage(restored, failedDocumentIds))
{
    public int Restored { get; } = restored;

    public IReadOnlyList<Guid> FailedDocumentIds { get; } = failedDocumentIds;

    private static string BuildMessage(int restored, IReadOnlyList<Guid> failed)
    {
        var sample = string.Join(", ", failed.Take(10));
        var more = failed.Count > 10 ? $" and {failed.Count - 10} more" : string.Empty;

        return $"The database was restored and {restored} document files were written back, but {failed.Count} could not be: {sample}{more}. Their documents exist without a stored file.";
    }
}
