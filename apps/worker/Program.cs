using System.Text.Json;
using DocReader.Application;
using DocReader.Infrastructure;
using DocReader.Infrastructure.Persistence;
using DocReader.Worker;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

// Etapa 2: o worker consome a fila (ADR 0001) e processa cada documento pelo ocr-service, renovando
// a reserva do job a cada página (ADR 0002).
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

builder.Services.AddHostedService<ProcessingWorker>();
builder.Services.AddHostedService<QueueDepthReporter>();

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
