using DocReader.Domain.Processing;
using DocReader.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocReader.Worker;

/// <summary>
/// Publica periodicamente a profundidade da fila nos logs: quanto trabalho está represado, em
/// andamento, concluído ou falho. Roda ao lado do <see cref="ProcessingWorker"/> e só lê.
/// </summary>
public sealed class QueueDepthReporter(
    IServiceScopeFactory scopeFactory,
    ILogger<QueueDepthReporter> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        try
        {
            do
            {
                await ReportAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Worker encerrando.");
        }
    }

    private async Task ReportAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DocReaderDbContext>();

            var counts = await dbContext.ProcessingJobs
                .AsNoTracking()
                .GroupBy(job => job.Status)
                .Select(group => new { Status = group.Key, Count = group.Count() })
                .ToListAsync(ct);

            var pending = counts.FirstOrDefault(entry => entry.Status == ProcessingJobStatus.Pending)?.Count ?? 0;
            var running = counts.FirstOrDefault(entry => entry.Status == ProcessingJobStatus.Running)?.Count ?? 0;
            var failed = counts.FirstOrDefault(entry => entry.Status == ProcessingJobStatus.Failed)?.Count ?? 0;
            var completed = counts.FirstOrDefault(entry => entry.Status == ProcessingJobStatus.Completed)?.Count ?? 0;

            logger.LogInformation(
                "Profundidade da fila. pending={Pending} running={Running} completed={Completed} failed={Failed}",
                pending,
                running,
                completed,
                failed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Banco indisponível não pode derrubar o worker: o health check já reporta o estado.
            logger.LogWarning(exception, "Não foi possível ler a profundidade da fila.");
        }
    }
}
