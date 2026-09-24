using System.Text.Json;
using DocReader.Worker;

// Stage 1 of the execution plan ships the worker as a stub: it is part of the compose topology and it
// answers health probes, but it does not consume the queue yet. Stage 2 adds the consumer described in
// docs/adr/0001-postgresql-como-fila.md, using FOR UPDATE SKIP LOCKED over processing_jobs.
var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});

builder.Services.AddHealthChecks();
builder.Services.AddHostedService<StageOneHeartbeat>();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

app.MapGet("/", () => Results.Json(new
{
    service = "worker",
    stage = 1,
    role = "stub",
    note = "Queue consumption starts in stage 2 of the execution plan."
}));

await app.RunAsync();
