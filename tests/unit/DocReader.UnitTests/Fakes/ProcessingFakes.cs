using DocReader.Application.Abstractions;
using DocReader.Domain.Documents;
using DocReader.Domain.Extractions;
using DocReader.Domain.Processing;

namespace DocReader.UnitTests.Fakes;

/// <summary>
/// Queue that records what the processor asked of it. The lock behaviour is scripted so a test can
/// take the job away from the worker at an exact point.
/// </summary>
public sealed class FakeProcessingQueue : IProcessingQueue
{
    /// <summary>Heartbeats answered true before the lock is "lost". Null keeps the lock forever.</summary>
    public int? HeartbeatsBeforeLockIsLost { get; set; }

    /// <summary>When false the failure is reported as not applied, as if another attempt owned the job.</summary>
    public bool FailIsApplied { get; set; } = true;

    public List<(int PagesCompleted, int PageCount)> Heartbeats { get; } = [];

    public List<(ProcessingJob Job, ProcessingError Error)> Failures { get; } = [];

    public List<ProcessingJob> Released { get; } = [];

    public Task EnqueueAsync(Guid documentId, CancellationToken ct) => throw new NotSupportedException();

    public Task<ProcessingJob?> AcquireNextAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> HeartbeatAsync(ProcessingJob job, int pagesCompleted, int pageCount, CancellationToken ct)
    {
        var owned = HeartbeatsBeforeLockIsLost is null || Heartbeats.Count < HeartbeatsBeforeLockIsLost;
        Heartbeats.Add((pagesCompleted, pageCount));

        return Task.FromResult(owned);
    }

    public Task CompleteAsync(ProcessingJob job, CancellationToken ct) => throw new NotSupportedException();

    public Task<JobFailureOutcome> FailAsync(ProcessingJob job, ProcessingError error, CancellationToken ct)
    {
        Failures.Add((job, error));

        return Task.FromResult(FailIsApplied
            ? new JobFailureOutcome(Applied: true, WillRetry: error.IsTransient, AvailableAt: null)
            : new JobFailureOutcome(Applied: false, WillRetry: false, AvailableAt: null));
    }

    public Task ReleaseAsync(ProcessingJob job, CancellationToken ct)
    {
        Released.Add(job);
        return Task.CompletedTask;
    }
}

public sealed record CompletedCall(
    DocumentExtraction Extraction,
    string DetectedType,
    decimal? Confidence,
    string? Details);

public sealed class FakeProcessingStore(Document? document) : IDocumentProcessingStore
{
    /// <summary>What CompleteAsync answers; false plays a worker that lost its lock at the very end.</summary>
    public bool CompleteResult { get; set; } = true;

    public List<(DocumentStatus Stage, string EventType, string? Details)> Stages { get; } = [];

    public List<(string EventType, string? Details)> Progress { get; } = [];

    public List<CompletedCall> Completed { get; } = [];

    public Task<Document?> FindDocumentAsync(Guid documentId, CancellationToken ct) => Task.FromResult(document);

    public Task AdvanceStageAsync(
        Guid documentId,
        DocumentStatus stage,
        string eventType,
        string? details,
        CancellationToken ct)
    {
        Stages.Add((stage, eventType, details));
        return Task.CompletedTask;
    }

    public Task RecordProgressAsync(Guid documentId, string eventType, string? details, CancellationToken ct)
    {
        Progress.Add((eventType, details));
        return Task.CompletedTask;
    }

    public Task<bool> CompleteAsync(
        ProcessingJob job,
        DocumentExtraction extraction,
        string detectedDocumentType,
        decimal? classificationConfidence,
        string? classificationDetails,
        CancellationToken ct)
    {
        Completed.Add(new CompletedCall(extraction, detectedDocumentType, classificationConfidence, classificationDetails));
        return Task.FromResult(CompleteResult);
    }
}

/// <summary>
/// OCR provider whose behaviour the test writes. The default reads every page from a script and
/// notifies the progress observer after each one, like the real provider.
/// </summary>
public sealed class ScriptedOcrProvider : IDocumentOcrProvider
{
    public Func<DocumentContent, OcrOptions, IOcrProgress?, CancellationToken, Task<OcrResult>>? Behaviour { get; set; }

    public OcrOptions? ReceivedOptions { get; private set; }

    public string ProviderName => "scripted";

    public Task<OcrResult> AnalyzeAsync(
        DocumentContent document,
        OcrOptions options,
        IOcrProgress? progress,
        CancellationToken ct)
    {
        ReceivedOptions = options;

        return Behaviour is null
            ? throw new InvalidOperationException("The test did not script the provider.")
            : Behaviour(document, options, progress, ct);
    }

    /// <summary>One entry per page, each a list of lines; every line becomes a block of confidence 0.99.</summary>
    public static ScriptedOcrProvider ReadingPages(params string[][] pages) =>
        new()
        {
            Behaviour = async (document, _, progress, ct) =>
            {
                var result = new List<OcrPage>();

                for (var index = 0; index < pages.Length; index++)
                {
                    var lines = pages[index];
                    var blocks = lines
                        .Select(line => new OcrBlock(line, 0.99m, [1, 2, 30, 2, 30, 12, 1, 12]))
                        .ToArray();

                    result.Add(new OcrPage(index + 1, string.Join('\n', lines), blocks));

                    if (progress is not null)
                    {
                        await progress.OnPageCompletedAsync(
                            new OcrPageProgress(index + 1, pages.Length, TimeSpan.FromMilliseconds(1500), blocks.Length),
                            ct);
                    }
                }

                return new OcrResult("scripted", "scripted-1.0", result, "{\"pages\":[]}");
            }
        };
}
