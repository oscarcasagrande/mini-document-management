using System.Globalization;
using System.Text.Json;
using DocReader.Application.Abstractions;
using DocReader.Application.Classification;
using DocReader.Application.Errors;
using DocReader.Application.Options;
using DocReader.Domain.Documents;
using DocReader.Domain.Extractions;
using DocReader.Domain.Processing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocReader.Application.Processing;

/// <summary>
/// Runs one attempt of a processing job through the pipeline of RF-007: prepare, read every page,
/// classify, extract, persist. The worker loop only acquires jobs and hands them here.
///
/// Rules this class enforces:
/// - the lock is renewed after every page (ADR 0002), and a worker that finds it no longer owns the
///   job drops everything it holds and writes nothing;
/// - the result is persisted in one transaction together with the job completion, so a partial
///   result never shows up as COMPLETED;
/// - a failure is either transient (retry with backoff) or definitive, and the message that reaches
///   the document is orientation, never an exception dump or document content;
/// - nothing here logs document content: only ids, stages, counts and durations.
/// </summary>
public sealed class DocumentProcessor(
    IProcessingQueue queue,
    IDocumentProcessingStore store,
    IFileStorage storage,
    IDocumentOcrProvider ocrProvider,
    IDocumentClassifier classifier,
    IEnumerable<IDocumentExtractor> extractors,
    IOptions<ProcessingQueueOptions> queueOptions,
    IOptions<OcrProviderOptions> ocrOptions,
    TimeProvider timeProvider,
    ILogger<DocumentProcessor> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task ProcessAsync(ProcessingJob job, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var correlationId = Guid.NewGuid().ToString("N");
        var startedAt = timeProvider.GetTimestamp();

        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["correlationId"] = correlationId,
            ["jobId"] = job.Id,
            ["documentId"] = job.DocumentId
        });

        using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        attemptTimeout.CancelAfter(queueOptions.Value.ProcessingTimeout);

        try
        {
            await RunAsync(job, correlationId, startedAt, attemptTimeout.Token).ConfigureAwait(false);
        }
        catch (JobLockLostException)
        {
            // Another attempt owns the document now. Failing or completing here would overwrite it.
            logger.LogWarning(
                "Worker lost the lock of its job and dropped the attempt. attempt={Attempt} durationMs={DurationMs}",
                job.AttemptCount,
                ElapsedMs(startedAt));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Clean shutdown: hand the job back at once instead of leaving it locked until the
            // heartbeat timeout expires.
            await queue.ReleaseAsync(job, CancellationToken.None).ConfigureAwait(false);
            logger.LogInformation(
                "Worker is stopping; the job was released without counting the attempt. durationMs={DurationMs}",
                ElapsedMs(startedAt));
        }
        catch (OperationCanceledException)
        {
            await FailAsync(
                job,
                new ProcessingError(
                    "PROCESSING_TIMEOUT",
                    "Processing took longer than the configured limit for one attempt.",
                    IsTransient: true),
                startedAt).ConfigureAwait(false);
        }
        catch (OcrProviderException exception)
        {
            await FailAsync(
                job,
                new ProcessingError(exception.Code, exception.Message, exception.IsTransient),
                startedAt).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Only the type is logged with the exception: its message could quote document content.
            logger.LogError(
                "Unexpected error while processing. errorType={ErrorType} durationMs={DurationMs}",
                exception.GetType().Name,
                ElapsedMs(startedAt));

            await FailAsync(
                job,
                new ProcessingError(
                    "PROCESSING_ERROR",
                    "An unexpected error interrupted the processing. Check the worker logs with the correlation id.",
                    IsTransient: true),
                startedAt).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(ProcessingJob job, string correlationId, long startedAt, CancellationToken ct)
    {
        var document = await store.FindDocumentAsync(job.DocumentId, ct).ConfigureAwait(false);
        if (document is null)
        {
            logger.LogWarning("The document of the job no longer exists; nothing to process.");
            return;
        }

        logger.LogInformation(
            "Processing started. protocol={Protocol} attempt={Attempt} mimeType={MimeType} sizeBytes={SizeBytes} pageCount={PageCount}",
            document.Protocol,
            job.AttemptCount,
            document.MimeType,
            document.SizeBytes,
            document.PageCount);

        await store.AdvanceStageAsync(
            document.Id,
            DocumentStatus.Preprocessing,
            DocumentEventTypes.PreprocessingStarted,
            Details($"attempt={job.AttemptCount}", $"pages={document.PageCount}"),
            ct).ConfigureAwait(false);

        // Records the page count on the job and proves the lock is still ours before any real work.
        await EnsureLockAsync(job, pagesCompleted: 0, document.PageCount, ct).ConfigureAwait(false);

        var ocrResult = await ReadPagesAsync(job, document, correlationId, ct).ConfigureAwait(false);

        await store.AdvanceStageAsync(
            document.Id,
            DocumentStatus.Classifying,
            DocumentEventTypes.ClassificationStarted,
            null,
            ct).ConfigureAwait(false);

        var classification = classifier.Classify(ocrResult);

        StructuredExtraction? structured = null;
        IDocumentExtractor? extractor = null;

        if (classification.IsKnown)
        {
            extractor = extractors.FirstOrDefault(candidate =>
                string.Equals(candidate.DocumentType, classification.DocumentType, StringComparison.Ordinal));
        }

        if (extractor is not null)
        {
            await store.AdvanceStageAsync(
                document.Id,
                DocumentStatus.Extracting,
                DocumentEventTypes.ExtractionStarted,
                $"type={extractor.DocumentType}",
                ct).ConfigureAwait(false);

            structured = await extractor.ExtractAsync(ocrResult, ct).ConfigureAwait(false);
        }

        var extraction = BuildExtraction(job, ocrResult, classification, extractor, structured);

        var persisted = await store.CompleteAsync(
            job,
            extraction,
            classification.DocumentType,
            classification.Confidence,
            ClassificationDetails(classification, document.ExpectedDocumentType),
            ct).ConfigureAwait(false);

        if (!persisted)
        {
            throw new JobLockLostException(job.Id);
        }

        logger.LogInformation(
            "Processing completed. protocol={Protocol} detectedType={DetectedType} classificationConfidence={Confidence} overallConfidence={OverallConfidence} pageCount={PageCount} durationMs={DurationMs}",
            document.Protocol,
            classification.DocumentType,
            classification.Confidence,
            extraction.OverallConfidence,
            ocrResult.Pages.Count,
            ElapsedMs(startedAt));
    }

    private async Task<OcrResult> ReadPagesAsync(
        ProcessingJob job,
        Document document,
        string correlationId,
        CancellationToken ct)
    {
        Stream original;
        try
        {
            original = await storage.OpenReadAsync(document.StorageKey, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            logger.LogError("The stored original is missing. storageKey={StorageKey}", document.StorageKey);
            throw new OcrProviderException(
                "ORIGINAL_MISSING",
                "The stored original file is missing, so it cannot be read. Upload the document again.",
                isTransient: false);
        }

        await using (original.ConfigureAwait(false))
        {
            await store.AdvanceStageAsync(
                document.Id,
                DocumentStatus.OcrRunning,
                DocumentEventTypes.OcrStarted,
                $"provider={ocrProvider.ProviderName}",
                ct).ConfigureAwait(false);

            var options = new OcrOptions(
                ocrOptions.Value.Languages,
                DetectLayout: false,
                ocrOptions.Value.PageTimeout,
                correlationId);

            var progress = new PageProgress(this, job, document);

            return await ocrProvider
                .AnalyzeAsync(new DocumentContent(document.Id, document.MimeType, document.PageCount, original), options, progress, ct)
                .ConfigureAwait(false);
        }
    }

    private DocumentExtraction BuildExtraction(
        ProcessingJob job,
        OcrResult ocrResult,
        ClassificationResult classification,
        IDocumentExtractor? extractor,
        StructuredExtraction? structured)
    {
        var pageTexts = ocrResult.Pages.Select(page => new { pageNumber = page.PageNumber, text = page.Text });
        var rawText = string.Join("\n\n", ocrResult.Pages.Select(page => page.Text));

        string? structuredJson = null;
        var fields = new List<ExtractedField>();

        if (structured is not null)
        {
            structuredJson = JsonSerializer.Serialize(
                structured.Fields.ToDictionary(field => field.Key, field => field.Value.Normalized),
                Json);

            foreach (var (path, value) in structured.Fields)
            {
                fields.Add(ExtractedField.Create(
                    path,
                    value.Raw,
                    value.Normalized,
                    value.Confidence,
                    value.PageNumber,
                    JsonSerializer.Serialize(value.BoundingBox, Json),
                    value.ValidationStatus,
                    JsonSerializer.Serialize(value.ValidationMessages ?? [], Json)));
            }
        }

        return DocumentExtraction.Create(
            job.DocumentId,
            job.Id,
            ocrResult.ProviderName,
            ocrResult.ModelVersion,
            classifier.Version,
            extractor?.Version,
            structured?.SchemaVersion,
            rawText,
            JsonSerializer.Serialize(pageTexts, Json),
            ocrResult.RawResult,
            structuredJson,
            structured?.OverallConfidence,
            timeProvider.GetUtcNow(),
            fields);
    }

    private static string ClassificationDetails(ClassificationResult classification, string? expectedType)
    {
        var details = new List<string>
        {
            $"type={classification.DocumentType}",
            $"confidence={FormatConfidence(classification.Confidence)}",
            $"signals={classification.Signals.Count}",
            $"classifier={classification.ClassifierVersion}"
        };

        // RF-010: the expected type is only a hint, and a mismatch is recorded, not corrected.
        if (!string.IsNullOrWhiteSpace(expectedType))
        {
            var divergent = !string.Equals(expectedType, classification.DocumentType, StringComparison.Ordinal);
            details.Add($"expected={expectedType}");
            details.Add($"divergent={(divergent ? "true" : "false")}");
        }

        return string.Join(' ', details);
    }

    private async Task EnsureLockAsync(ProcessingJob job, int pagesCompleted, int pageCount, CancellationToken ct)
    {
        var owned = await queue.HeartbeatAsync(job, pagesCompleted, pageCount, ct).ConfigureAwait(false);
        if (!owned)
        {
            throw new JobLockLostException(job.Id);
        }
    }

    private async Task FailAsync(ProcessingJob job, ProcessingError error, long startedAt)
    {
        var outcome = await queue.FailAsync(job, error, CancellationToken.None).ConfigureAwait(false);

        if (!outcome.Applied)
        {
            logger.LogWarning(
                "Worker lost the lock of its job before it could report the failure. errorCode={ErrorCode} attempt={Attempt}",
                error.Code,
                job.AttemptCount);
            return;
        }

        if (outcome.WillRetry)
        {
            logger.LogWarning(
                "Attempt failed, retry scheduled. errorCode={ErrorCode} attempt={Attempt} availableAt={AvailableAt} durationMs={DurationMs}",
                error.Code,
                job.AttemptCount,
                outcome.AvailableAt,
                ElapsedMs(startedAt));
            return;
        }

        logger.LogError(
            "Attempt failed definitively. errorCode={ErrorCode} attempt={Attempt} durationMs={DurationMs}",
            error.Code,
            job.AttemptCount,
            ElapsedMs(startedAt));
    }

    private long ElapsedMs(long startedAt) =>
        (long)timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;

    private static string Details(params string[] parts) => string.Join(' ', parts);

    private static string FormatConfidence(decimal? confidence) =>
        confidence?.ToString("0.####", CultureInfo.InvariantCulture) ?? "none";

    private async Task OnPageCompletedAsync(
        ProcessingJob job,
        Document document,
        OcrPageProgress page,
        CancellationToken ct)
    {
        await EnsureLockAsync(job, page.PageNumber, page.PageCount, ct).ConfigureAwait(false);

        await store.RecordProgressAsync(
            document.Id,
            DocumentEventTypes.OcrPageCompleted,
            Details(
                $"page={page.PageNumber}/{page.PageCount}",
                $"durationMs={(long)page.Duration.TotalMilliseconds}",
                $"blocks={page.BlockCount}"),
            ct).ConfigureAwait(false);
    }

    /// <summary>Renews the lock and publishes progress after each page the provider finishes.</summary>
    private sealed class PageProgress(DocumentProcessor owner, ProcessingJob job, Document document) : IOcrProgress
    {
        public Task OnPageCompletedAsync(OcrPageProgress page, CancellationToken ct) =>
            owner.OnPageCompletedAsync(job, document, page, ct);
    }
}
