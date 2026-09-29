using DocReader.Api.Contracts.V1;
using DocReader.Domain.Backup;

namespace DocReader.Api.Mapping;

/// <summary>Backup and restore jobs to their public shape.</summary>
public static class BackupResponseMapper
{
    public static string JobUrl(Guid id) => $"/api/v1/admin/backup/{id}";

    public static string RestoreJobUrl(Guid id) => $"/api/v1/admin/restore/{id}";

    public static BackupJobResponse ToResponse(BackupJob job) => new(
        job.Id,
        job.Status,
        job.RequestedAt,
        job.StartedAt,
        job.CompletedAt,
        job.StorageRepositoryId,
        job.StorageKey,
        job.FileName,
        job.SizeBytes,
        job.ChecksumSha256,
        job.DocumentCount,
        job.FilesArchived,
        job.ErrorMessage,
        job.Status == BackupJobStatus.Completed ? JobUrl(job.Id) + "/content" : null);

    public static RestoreJobResponse ToResponse(RestoreJob job) => new(
        job.Id,
        job.Status,
        job.RequestedAt,
        job.StartedAt,
        job.CompletedAt,
        job.ArchiveSizeBytes,
        job.ArchiveChecksumSha256,
        job.DocumentCount,
        job.FilesRestored,
        job.ErrorMessage);
}
