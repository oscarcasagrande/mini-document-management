using System.ComponentModel.DataAnnotations;
using DocReader.Domain.StorageMigrations;

namespace DocReader.Api.Contracts.V1;

public sealed class CreateStorageMigrationRequest
{
    /// <summary>Repository the documents are copied from. Nothing is deleted from it.</summary>
    [Required]
    public Guid? SourceRepositoryId { get; init; }

    /// <summary>Repository the documents are copied to and repointed at. Must be active and differ from the source.</summary>
    [Required]
    public Guid? TargetRepositoryId { get; init; }

    /// <summary>Which documents of the source to move; omit to move every document currently on it.</summary>
    public StorageMigrationDocumentFilterRequest? DocumentFilter { get; init; }
}

/// <summary>Narrows the documents a migration moves. Every criterion is optional and they combine with AND.</summary>
public sealed class StorageMigrationDocumentFilterRequest
{
    /// <summary>Detected document type (BR_CPF_CARD, BR_CIN, BR_CNH, ...).</summary>
    [MaxLength(64)]
    public string? DocumentType { get; init; }

    /// <summary>Id of the product or service the documents were uploaded for.</summary>
    public Guid? ProductServiceId { get; init; }

    /// <summary>Inclusive lower bound of the upload instant, ISO 8601.</summary>
    public DateTimeOffset? UploadedFrom { get; init; }

    /// <summary>Inclusive upper bound of the upload instant, ISO 8601.</summary>
    public DateTimeOffset? UploadedTo { get; init; }
}

/// <summary>The filter a migration job was created with.</summary>
/// <param name="DocumentType">Detected document type, or null for any.</param>
/// <param name="ProductServiceId">Product or service, or null for any.</param>
/// <param name="UploadedFrom">Inclusive lower bound of the upload instant, in UTC, or null.</param>
/// <param name="UploadedTo">Inclusive upper bound of the upload instant, in UTC, or null.</param>
public sealed record StorageMigrationDocumentFilterResponse(
    string? DocumentType,
    Guid? ProductServiceId,
    DateTimeOffset? UploadedFrom,
    DateTimeOffset? UploadedTo);

/// <summary>One run that moves documents from a storage repository to another.</summary>
/// <param name="Id">Identity of the job.</param>
/// <param name="SourceRepositoryId">Repository the documents are copied from.</param>
/// <param name="TargetRepositoryId">Repository the documents are copied to.</param>
/// <param name="DocumentFilter">The filter the job was created with; null when it moves every document of the source.</param>
/// <param name="Status">PENDING, RUNNING, COMPLETED, FAILED or CANCELLED.</param>
/// <param name="RequestedAt">When the job was requested, in UTC.</param>
/// <param name="StartedAt">When the worker picked it up, in UTC; null while still pending.</param>
/// <param name="CompletedAt">When the job ended (completed, failed or cancelled), in UTC; null while pending or running.</param>
/// <param name="DocumentsMigrated">Documents moved to the target so far.</param>
/// <param name="DocumentsFailed">Per-document failures so far; those documents stay on the source.</param>
public sealed record StorageMigrationJobResponse(
    Guid Id,
    Guid SourceRepositoryId,
    Guid TargetRepositoryId,
    StorageMigrationDocumentFilterResponse? DocumentFilter,
    StorageMigrationStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int DocumentsMigrated,
    int DocumentsFailed);

/// <summary>One move of a document between storage repositories, read from its timeline.</summary>
/// <param name="FromRepository">Id of the repository the document was stored in.</param>
/// <param name="ToRepository">Id of the repository it was moved to.</param>
/// <param name="MigratedAt">When it was moved, in UTC.</param>
public sealed record StorageMigrationHistoryEntryResponse(
    Guid FromRepository,
    Guid ToRepository,
    DateTimeOffset MigratedAt);
