using System.Text.Json;
using DocReader.Application;
using DocReader.Application.Options;
using DocReader.Infrastructure;
using DocReader.Infrastructure.Persistence;
using DocReader.Infrastructure.Queue;
using DocReader.Worker;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

// O worker consome a fila (ADR 0001) e processa cada documento pelo ocr-service, renovando a reserva
// do job a cada página (ADR 0002).
var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});

builder.Services.AddDocReaderApplication(builder.Configuration);
builder.Services.AddDocReaderInfrastructure(builder.Configuration);
builder.Services.AddDocReaderProcessing(builder.Configuration);
builder.Services.AddDocReaderOcrProvider();

builder.Services.AddSingleton<IValidateOptions<PurgeOptions>, PurgeOptionsValidator>();

// ADR 0004: the RabbitMQ connection/consumer and the outbox publisher only exist in this process
// (apps/api never consumes jobs) and only when QUEUE_PROVIDER=RabbitMQ - in Postgres mode (the
// default, ADR 0001) neither is registered, so no broker connection is ever attempted. Read the same
// way AddDocReaderInfrastructure reads it, straight from configuration, so the two agree without a
// temporary service provider.
var queueProvider = QueueProviderOptions.FromConfigurationValue(builder.Configuration[QueueProviderOptions.ConfigurationKey]);
if (queueProvider.Provider == QueueProvider.RabbitMq)
{
    builder.Services.AddDocReaderRabbitMqConsumer();
    builder.Services.AddHostedService<RabbitMqOutboxPublisher>();
}

builder.Services.AddHostedService<ProcessingWorker>();
builder.Services.AddHostedService<PurgeExpiredDocumentsJob>();
builder.Services.AddHostedService<WebhookDispatcher>();
builder.Services.AddHostedService<RetentionReapplyWorker>();
builder.Services.AddHostedService<GdprDeletionWorker>();
builder.Services.AddHostedService<QueueDepthReporter>();
builder.Services.AddHostedService<StorageMigrationWorker>();
builder.Services.AddHostedService<BackupWorker>();
builder.Services.AddHostedService<RestoreWorker>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<DocReaderDbContext>("postgres", tags: ["ready"]);

var app = builder.Build();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.MapGet("/", () => Results.Json(new
{
    service = "worker",
    stage = 2,
    queueConsumption = "enabled",
    ocrEngine = "PP-OCRv5 mobile (ADR 0002)"
}));

await app.RunAsync();
