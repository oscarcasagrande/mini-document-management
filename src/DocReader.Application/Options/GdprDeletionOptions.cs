using System.ComponentModel.DataAnnotations;

namespace DocReader.Application.Options;

/// <summary>How a GDPR/LGPD deletion request is auto-approved when nobody decides in time (worker only).</summary>
public sealed class GdprDeletionOptions
{
    public const string SectionName = "DocReader:GdprDeletion";

    /// <summary>
    /// Identity recorded as <c>approvedBy</c> when the worker approves a request itself, instead of an operator.
    /// Deliberately not a real user id, so it is unmistakable in the timeline and the audit log.
    /// </summary>
    public const string AutoApprovedBy = "system:auto-approve-24h";

    /// <summary>Hours a PENDING request waits for a manual decision before the worker approves it on its own.</summary>
    [Range(0.01, 24 * 30)]
    public double AutoApproveAfterHours { get; set; } = 24;

    public TimeSpan AutoApproveAfter => TimeSpan.FromHours(AutoApproveAfterHours);
}
