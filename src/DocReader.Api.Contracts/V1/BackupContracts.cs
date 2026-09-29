using System.ComponentModel.DataAnnotations;
using DocReader.Domain.Backup;
using Microsoft.AspNetCore.Http;

namespace DocReader.Api.Contracts.V1;

/// <summary>Where to save the backup archive. The whole body is optional.</summary>
public sealed class CreateBackupRequest
{
    /// <summary>
    /// Storage repository the archive is saved in: a FILE_SYSTEM, AZURE_BLOB_STORAGE or AWS_S3 one. Omit for the default
    /// repository. A DATABASE repository is refused (the backup would live inside the database it protects).
    /// </summary>
    public Guid? StorageRepositoryId { get; init; }
}

/// <summary>One backup run and, once completed, where its archive is.</summary>
/// <param name="Id">Identity of the run.</param>
/// <param name="Status">PENDING, RUNNING, COMPLETED or FAILED.</param>
/// <param name="RequestedAt">When the backup was requested, in UTC.</param>
/// <param name="StartedAt">When the worker picked it up, in UTC; null while pending.</param>
/// <param name="CompletedAt">When it finished (successfully or not), in UTC; null while pending or running.</param>
/// <param name="StorageRepositoryId">Repository the archive is saved in.</param>
/// <param name="StorageKey">Key of the archive in that repository; null until completed.</param>
/// <param name="FileName">Download name of the archive; null until completed.</param>
/// <param name="SizeBytes">Size of the archive; null until completed.</param>
/// <param name="ChecksumSha256">SHA-256 (hex) of the archive file; null until completed.</param>
/// <param name="DocumentCount">Documents listed in the archive's storage manifest.</param>
/// <param name="FilesArchived">Document files copied into the archive (file system and database repositories only; cloud-stored documents keep a manifest entry only).</param>
/// <param name="ErrorMessage">Why it failed; null unless FAILED.</param>
/// <param name="DownloadUrl">Where to download the archive; null until completed.</param>
public sealed record BackupJobResponse(
    Guid Id,
    BackupJobStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    Guid StorageRepositoryId,
    string? StorageKey,
    string? FileName,
    long? SizeBytes,
    string? ChecksumSha256,
    int? DocumentCount,
    int? FilesArchived,
    string? ErrorMessage,
    string? DownloadUrl);

/// <summary>The archive to restore.</summary>
public sealed class RestoreBackupRequest
{
    /// <summary>A <c>.tar.gz</c> produced by <c>POST /api/v1/admin/backup</c> of an installation with the same encryption key.</summary>
    [Required]
    public IFormFile? File { get; init; }
}

/// <summary>One restore run.</summary>
/// <param name="Id">Identity of the run.</param>
/// <param name="Status">PENDING, RUNNING, COMPLETED or FAILED.</param>
/// <param name="RequestedAt">When the archive was uploaded and verified, in UTC.</param>
/// <param name="StartedAt">When the worker picked it up, in UTC; null while pending.</param>
/// <param name="CompletedAt">When it finished (successfully or not), in UTC; null while pending or running.</param>
/// <param name="ArchiveSizeBytes">Size of the uploaded archive.</param>
/// <param name="ArchiveChecksumSha256">SHA-256 (hex) of the uploaded archive file.</param>
/// <param name="DocumentCount">Documents listed in the archive's storage manifest.</param>
/// <param name="FilesRestored">Document files written back to their storage repository; null until finished.</param>
/// <param name="ErrorMessage">Why it failed; null unless FAILED.</param>
public sealed record RestoreJobResponse(
    Guid Id,
    RestoreJobStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long ArchiveSizeBytes,
    string ArchiveChecksumSha256,
    int DocumentCount,
    int? FilesRestored,
    string? ErrorMessage);
