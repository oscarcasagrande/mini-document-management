using System.Text.Json;
using DocReader.Application;
using DocReader.Infrastructure;
using DocReader.Infrastructure.Persistence;
using DocReader.Worker;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

// Etapa 2 em andamento. A fila já está implementada em PostgresProcessingQueue, com
// FOR UPDATE SKIP LOCKED conforme a ADR 0001, mas o laço de consumo só é ligado junto com o
// provedor de OCR: ligar antes marcaria documentos como processados sem processá-los.
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
    queueConsumption = "disabled",
    reason = "aguardando escolha da engine de OCR"
}));

await app.RunAsync();
