using DocReader.Domain.Processing;

namespace DocReader.Api.Contracts.V1;

/// <summary>
/// Where the asynchronous work of a document stands: which attempt, how many pages were read and,
/// when a retry is scheduled, when it will run. It is what makes a long document look alive instead
/// of stuck.
/// </summary>
/// <param name="JobStatus">PENDING, RUNNING, COMPLETED or FAILED.</param>
/// <param name="Attempt">Attempt in course or the last one that ran; 0 before the first is acquired.</param>
/// <param name="MaxAttempts">Attempts allowed before a transient failure becomes definitive.</param>
/// <param name="PagesCompleted">Pages already read in the current attempt.</param>
/// <param name="PageCount">Pages the attempt has to read, known once the worker starts.</param>
/// <param name="NextAttemptAt">Earliest moment of the next attempt, in UTC, while a retry is scheduled.</param>
public sealed record DocumentProcessingResponse(
    ProcessingJobStatus JobStatus,
    int Attempt,
    int MaxAttempts,
    int PagesCompleted,
    int? PageCount,
    DateTimeOffset? NextAttemptAt);
