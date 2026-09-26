using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DocReader.Application.Abstractions;
using DocReader.Application.Errors;
using DocReader.Application.Options;
using DocReader.Application.Retention;
using DocReader.Application.Storage;
using DocReader.Domain.Catalog;
using DocReader.Domain.Documents;
using DocReader.Domain.Files;
using DocReader.Domain.Idempotency;
using DocReader.Domain.Processing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocReader.Application.Documents;

/// <summary>
/// Accepts an upload: validates it, stores the blob and persists document plus processing job in a
/// single transaction. The OCR itself never runs inside the HTTP request (RF-007).
/// </summary>
public sealed class DocumentUploadService(
    IFileStorage storage,
    IDocumentRepository repository,
    IProductServiceRepository productServices,
    RetentionService retention,
    StorageRepositoryResolver storageResolver,
    IIdempotencyStore idempotencyStore,
    IProtocolGenerator protocolGenerator,
    IPageCounter pageCounter,
    IOptions<UploadOptions> uploadOptions,
    IOptions<IdempotencyOptions> idempotencyOptions,
    TimeProvider timeProvider,
    ILogger<DocumentUploadService> logger)
{
    private static readonly JsonSerializerOptions SnapshotSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly UploadOptions _upload = uploadOptions.Value;
    private readonly IdempotencyOptions _idempotency = idempotencyOptions.Value;

    public async Task<UploadDocumentResult> UploadAsync(UploadDocumentCommand command, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var content = command.Content;

        if (!content.CanSeek)
        {
            throw new InvalidOperationException("Upload content must be a seekable stream.");
        }

        var expectedDocumentType = NormalizeOptionalField(
            command.ExpectedDocumentType, _upload.MaxExpectedDocumentTypeLength, "expectedDocumentType");
        var externalReference = NormalizeOptionalField(
            command.ExternalReference, _upload.MaxExternalReferenceLength, "externalReference");
        var idempotencyKey = NormalizeIdempotencyKey(command.IdempotencyKey);
        var productService = await ResolveProductServiceAsync(command.ProductServiceCode, ct).ConfigureAwait(false);
        var fileName = SanitizeFileName(command.OriginalFileName);
        var retentionPolicy = await retention.ResolveAsync(expectedDocumentType, productService?.Id, ct).ConfigureAwait(false);
        var storageRepository = await storageResolver.ResolveForUploadAsync(productService, ct).ConfigureAwait(false);

        var sizeBytes = content.Length;
        if (sizeBytes == 0)
        {
            throw new UploadRejectedException(
                UploadRejectionReason.MissingFile,
                "EMPTY_FILE",
                "The uploaded file has no content.");
        }

        if (sizeBytes > _upload.MaxSizeBytes)
        {
            throw new UploadRejectedException(
                UploadRejectionReason.TooLarge,
                "FILE_TOO_LARGE",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The uploaded file has {sizeBytes} bytes and the limit is {_upload.MaxSizeBytes} bytes."));
        }

        var inspection = await InspectAsync(content, ct).ConfigureAwait(false);
        var mimeType = inspection.MimeType!;
        var extension = inspection.Extension!;

        if (!FileSignatureInspector.IsDeclaredTypeConsistent(command.DeclaredContentType, mimeType))
        {
            throw new UploadRejectedException(
                UploadRejectionReason.DeclaredTypeMismatch,
                "CONTENT_TYPE_MISMATCH",
                "The declared content type does not match the real signature of the file.");
        }

        var sha256 = await ComputeSha256Async(content, ct).ConfigureAwait(false);

        if (idempotencyKey is not null)
        {
            var replay = await TryReplayAsync(idempotencyKey, sha256, ct).ConfigureAwait(false);
            if (replay is not null)
            {
                return replay;
            }
        }

        var pageCount = await CountPagesAsync(content, mimeType, ct).ConfigureAwait(false);

        var documentId = Guid.CreateVersion7(now);
        var protocol = await protocolGenerator.NextAsync(now, ct).ConfigureAwait(false);

        content.Position = 0;
        var stored = await storage
            .SaveAsync(storageRepository.Id, content, new FileMetadata(documentId, extension, mimeType, now), ct)
            .ConfigureAwait(false);

        try
        {
            var document = Document.Accept(
                documentId,
                protocol,
                fileName,
                stored.StorageKey,
                mimeType,
                stored.SizeBytes,
                sha256,
                pageCount,
                command.Channel,
                externalReference,
                expectedDocumentType,
                now,
                productService?.Id,
                retentionPolicy,
                storageRepository.Id);

            document.MarkQueued(now);

            // The job row is the enqueue: it is written in the same transaction as the document, so a
            // document answered with 202 always has pending work (ADR 0001).
            var job = ProcessingJob.CreateForDocument(documentId, now);

            var record = idempotencyKey is null
                ? null
                : BuildIdempotencyRecord(idempotencyKey, sha256, document, now);

            await repository.AcceptAsync(document, job, record, ct).ConfigureAwait(false);

            logger.LogInformation(
                "Document accepted. documentId={DocumentId} protocol={Protocol} channel={Channel} mimeType={MimeType} sizeBytes={SizeBytes} pageCount={PageCount} storageKey={StorageKey}",
                documentId,
                protocol,
                command.Channel,
                mimeType,
                stored.SizeBytes,
                pageCount,
                stored.StorageKey);

            return new UploadDocumentResult(documentId, protocol, document.Status, Replayed: false);
        }
        catch
        {
            // Never leave a blob behind for a document that was not persisted.
            await SafeDeleteAsync(storageRepository.Id, stored.StorageKey).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The product or service the caller named, or null when none was named. An unknown or inactive one is a
    /// 422: the upload is well formed, but it points at something that cannot receive documents.
    /// </summary>
    private async Task<ProductService?> ResolveProductServiceAsync(string? code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var normalized = ProductService.NormalizeCode(code);
        var productService = normalized is null
            ? null
            : await productServices.FindByCodeAsync(normalized, ct).ConfigureAwait(false);

        if (productService is null)
        {
            throw new UploadRejectedException(
                UploadRejectionReason.UnprocessableContent,
                "PRODUCT_SERVICE_NOT_FOUND",
                $"There is no product or service with the code {code.Trim()}.");
        }

        return productService.Active
            ? productService
            : throw new UploadRejectedException(
                UploadRejectionReason.UnprocessableContent,
                "PRODUCT_SERVICE_INACTIVE",
                $"The product or service {productService.Code} is inactive and does not accept documents.");
    }

    private async Task<UploadDocumentResult?> TryReplayAsync(string key, string sha256, CancellationToken ct)
    {
        var existing = await idempotencyStore.FindLiveAsync(key, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        if (!string.Equals(existing.FileSha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Idempotency-Key replayed with a different file. documentId={DocumentId}",
                existing.DocumentId);
            throw new IdempotencyConflictException(key);
        }

        var snapshot = DeserializeSnapshot(existing);
        logger.LogInformation(
            "Idempotency-Key replay answered from the stored record. documentId={DocumentId} protocol={Protocol}",
            snapshot.Id,
            snapshot.Protocol);

        var status = Enum.TryParse<DocumentStatus>(snapshot.Status, ignoreCase: true, out var parsed)
            ? parsed
            : DocumentStatus.Queued;

        return new UploadDocumentResult(snapshot.Id, snapshot.Protocol, status, Replayed: true);
    }

    private async Task<FileInspectionResult> InspectAsync(Stream content, CancellationToken ct)
    {
        var headerSize = (int)Math.Min(FileSignatureInspector.HeaderSize, content.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(headerSize);
        try
        {
            content.Position = 0;
            var read = await content
                .ReadAtLeastAsync(buffer.AsMemory(0, headerSize), headerSize, throwOnEndOfStream: false, ct)
                .ConfigureAwait(false);

            var inspection = FileSignatureInspector.Inspect(buffer.AsSpan(0, read));

            return inspection.Outcome switch
            {
                FileInspectionOutcome.Supported => inspection,
                FileInspectionOutcome.Blocked => throw new UploadRejectedException(
                    UploadRejectionReason.BlockedContent,
                    "BLOCKED_CONTENT",
                    $"The file signature ({inspection.SignatureName}) is not a document and was blocked."),
                FileInspectionOutcome.Empty => throw new UploadRejectedException(
                    UploadRejectionReason.MissingFile,
                    "EMPTY_FILE",
                    "The uploaded file has no content."),
                _ => throw new UploadRejectedException(
                    UploadRejectionReason.UnsupportedFormat,
                    "UNSUPPORTED_MEDIA_TYPE",
                    "Only PDF, PNG, JPEG and TIFF files are accepted.")
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string> ComputeSha256Async(Stream content, CancellationToken ct)
    {
        content.Position = 0;
        var hash = await SHA256.HashDataAsync(content, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private async Task<int> CountPagesAsync(Stream content, string mimeType, CancellationToken ct)
    {
        int pageCount;
        try
        {
            pageCount = await pageCounter.CountPagesAsync(content, mimeType, ct).ConfigureAwait(false);
        }
        catch (UnreadableDocumentException exception)
        {
            throw new UploadRejectedException(
                UploadRejectionReason.UnprocessableContent,
                "UNREADABLE_DOCUMENT",
                exception.Message);
        }

        if (pageCount > _upload.MaxPageCount)
        {
            throw new UploadRejectedException(
                UploadRejectionReason.UnprocessableContent,
                "PAGE_LIMIT_EXCEEDED",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The document has {pageCount} pages and the limit is {_upload.MaxPageCount} pages."));
        }

        return pageCount;
    }

    private IdempotencyRecord BuildIdempotencyRecord(
        string key,
        string sha256,
        Document document,
        DateTimeOffset now)
    {
        var snapshot = new UploadResponseSnapshot(document.Id, document.Protocol, document.Status.ToString());

        return new IdempotencyRecord
        {
            Key = key,
            FileSha256 = sha256,
            DocumentId = document.Id,
            ResponseStatus = 202,
            ResponseBody = JsonSerializer.Serialize(snapshot, SnapshotSerializerOptions),
            CreatedAt = now,
            ExpiresAt = now.Add(_idempotency.Ttl)
        };
    }

    private static UploadResponseSnapshot DeserializeSnapshot(IdempotencyRecord record)
    {
        var snapshot = JsonSerializer.Deserialize<UploadResponseSnapshot>(
            record.ResponseBody, SnapshotSerializerOptions);

        return snapshot ?? new UploadResponseSnapshot(
            record.DocumentId, string.Empty, nameof(DocumentStatus.Queued));
    }

    private async Task SafeDeleteAsync(Guid repositoryId, string storageKey)
    {
        try
        {
            await storage.DeleteAsync(repositoryId, storageKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to remove orphan blob after a rejected persistence. storageKey={StorageKey}",
                storageKey);
        }
    }

    private string? NormalizeIdempotencyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var trimmed = key.Trim();
        if (trimmed.Length > _idempotency.MaxKeyLength)
        {
            throw new UploadRejectedException(
                UploadRejectionReason.InvalidRequest,
                "INVALID_IDEMPOTENCY_KEY",
                $"Idempotency-Key must not exceed {_idempotency.MaxKeyLength} characters.");
        }

        if (trimmed.Any(char.IsControl))
        {
            throw new UploadRejectedException(
                UploadRejectionReason.InvalidRequest,
                "INVALID_IDEMPOTENCY_KEY",
                "Idempotency-Key must not contain control characters.");
        }

        return trimmed;
    }

    private static string? NormalizeOptionalField(string? value, int maxLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new UploadRejectedException(
                UploadRejectionReason.InvalidRequest,
                "INVALID_FIELD",
                $"Field {fieldName} must not exceed {maxLength} characters.");
        }

        return trimmed;
    }

    /// <summary>
    /// Keeps the file name as metadata only: path components and control characters are removed so
    /// the value is safe to echo in a header, and it never reaches the storage key.
    /// </summary>
    private string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "upload";
        }

        var candidate = fileName.Replace('\\', '/');
        var lastSeparator = candidate.LastIndexOf('/');
        if (lastSeparator >= 0)
        {
            candidate = candidate[(lastSeparator + 1)..];
        }

        var cleaned = new string(candidate.Where(character => !char.IsControl(character)).ToArray())
            .Trim()
            .TrimStart('.');

        if (cleaned.Length == 0)
        {
            return "upload";
        }

        return cleaned.Length > _upload.MaxFileNameLength
            ? cleaned[.._upload.MaxFileNameLength]
            : cleaned;
    }
}
