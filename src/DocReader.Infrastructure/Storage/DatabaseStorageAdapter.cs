using System.Globalization;
using System.Text.RegularExpressions;
using DocReader.Application.Abstractions;
using DocReader.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocReader.Infrastructure.Storage;

/// <summary>
/// The <see cref="Domain.Storage.StorageProvider.Database"/> adapter: keeps the bytes of a document in the
/// <c>document_blobs</c> table of the application database. Meant for small volumes and for setups without a shared
/// volume; the content is read and written whole, which the upload limit (25 MB by default) keeps reasonable.
///
/// The blob is written before the document row exists (the upload stores the file first), so the table has no foreign key
/// to documents. Deleting a document goes through the storage, which removes the blob.
/// </summary>
public sealed partial class DatabaseStorageAdapter(DocReaderDbContext dbContext) : IStorageAdapter
{
    public async Task<StoredFile> SaveAsync(Stream content, FileMetadata metadata, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        // Idempotent: storing the same document again replaces its blob instead of failing on the unique index.
        await dbContext.DocumentBlobs
            .Where(blob => blob.DocumentId == metadata.DocumentId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        var blob = new DocumentBlob
        {
            Id = Guid.CreateVersion7(metadata.CreatedAt),
            DocumentId = metadata.DocumentId,
            Content = bytes,
            CreatedAt = metadata.CreatedAt
        };

        dbContext.DocumentBlobs.Add(blob);

        try
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // The bytes are not kept in the change tracker of a scope that may live on: detach only the blob.
            dbContext.Entry(blob).State = EntityState.Detached;
        }

        return new StoredFile(KeyFor(metadata.DocumentId), bytes.Length);
    }

    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct)
    {
        var documentId = ParseKey(storageKey);

        var bytes = await dbContext.DocumentBlobs
            .AsNoTracking()
            .Where(blob => blob.DocumentId == documentId)
            .Select(blob => blob.Content)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return bytes is null
            ? throw new FileNotFoundException("Stored original is not available.", storageKey)
            : new MemoryStream(bytes, writable: false);
    }

    public async Task DeleteAsync(string storageKey, CancellationToken ct)
    {
        var documentId = ParseKey(storageKey);

        await dbContext.DocumentBlobs
            .Where(blob => blob.DocumentId == documentId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> IsWritableAsync(CancellationToken ct)
    {
        try
        {
            return await dbContext.Database.CanConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static string KeyFor(Guid documentId) => string.Create(CultureInfo.InvariantCulture, $"blobs/{documentId:D}");

    private static Guid ParseKey(string storageKey)
    {
        var match = KeyPattern().Match(storageKey);

        return match.Success && Guid.TryParse(match.Groups["id"].Value, out var id)
            ? id
            : throw new ArgumentException("Storage key has an unexpected shape.", nameof(storageKey));
    }

    [GeneratedRegex(@"^blobs/(?<id>[0-9a-fA-F-]{36})$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
