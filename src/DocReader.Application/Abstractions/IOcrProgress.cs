namespace DocReader.Application.Abstractions;

/// <summary>
/// Observer of an OCR run, notified after each page. It exists so the worker can prove it is alive
/// (heartbeat) and publish progress without the provider knowing about queues or databases.
/// </summary>
public interface IOcrProgress
{
    Task OnPageCompletedAsync(OcrPageProgress page, CancellationToken ct);
}

/// <param name="PageNumber">One based page that just finished.</param>
/// <param name="PageCount">Pages of the document.</param>
/// <param name="Duration">Time the provider took for this page.</param>
/// <param name="BlockCount">Text blocks read on the page. The content itself is never reported.</param>
public sealed record OcrPageProgress(int PageNumber, int PageCount, TimeSpan Duration, int BlockCount);
