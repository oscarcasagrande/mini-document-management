using System.Text.Json;
using System.Text.Json.Serialization;
using DocReader.Api.Errors;
using DocReader.Api.HealthChecks;
using DocReader.Api.Http;
using DocReader.Api.Options;
using DocReader.Api.Swagger;
using DocReader.Application;
using DocReader.Application.Options;
using DocReader.Infrastructure;
using DocReader.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------------------
// Logging: structured JSON on stdout, one event per line, no document content (PRD section 20).
// ---------------------------------------------------------------------------------------------
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});

builder.Services.AddOptions<SecurityOptions>()
    .Bind(builder.Configuration.GetSection(SecurityOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddOptions<OcrServiceOptions>()
    .Bind(builder.Configuration.GetSection(OcrServiceOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddDocReaderApplication(builder.Configuration);
builder.Services.AddDocReaderInfrastructure(builder.Configuration);

// ---------------------------------------------------------------------------------------------
// Upload limits: Kestrel and the multipart reader get a small margin above the business limit, so
// an oversized file is refused by the endpoint with a problem+json 413 instead of a raw connection
// error, while a grossly oversized body is still cut off by the server.
// ---------------------------------------------------------------------------------------------
var uploadLimits = builder.Configuration.GetSection(UploadOptions.SectionName).Get<UploadOptions>()
    ?? new UploadOptions();
var transportLimit = uploadLimits.MaxSizeBytes + (1024 * 1024);

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = transportLimit);

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = transportLimit;
    options.MultipartHeadersLengthLimit = 32 * 1024;
    options.ValueLengthLimit = 64 * 1024;
});

// ---------------------------------------------------------------------------------------------
// HTTP layer
// ---------------------------------------------------------------------------------------------
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options =>
        options.InvalidModelStateResponseFactory = InvalidModelStateResponse.Create)
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        options.JsonSerializerOptions.Converters.Add(new UtcDateTimeOffsetJsonConverter());
    });

builder.Services.AddSingleton<ProblemDetailsFactory, DocReaderProblemDetailsFactory>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

builder.Services.AddHttpClient(OcrServiceHealthCheck.HttpClientName, (provider, client) =>
{
    var ocr = provider.GetRequiredService<IOptions<OcrServiceOptions>>().Value;
    client.BaseAddress = new Uri(ocr.BaseUrl, UriKind.Absolute);
    client.Timeout = ocr.Timeout;
});

var corsOrigins = builder.Configuration
    .GetSection(SecurityOptions.SectionName)
    .Get<SecurityOptions>()?.AllowedCorsOrigins ?? [];

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    policy.WithExposedHeaders(CorrelationId.HeaderName, "Content-Disposition", "Location")
        .AllowAnyHeader()
        .AllowAnyMethod();

    if (corsOrigins.Length == 0)
    {
        // Nothing configured means no cross origin browser call is expected: the interface talks to
        // the API through its own BFF, server side.
        policy.SetIsOriginAllowed(_ => false);
        return;
    }

    policy.WithOrigins(corsOrigins);
}));

// ---------------------------------------------------------------------------------------------
// Health checks (RF-016)
// ---------------------------------------------------------------------------------------------
builder.Services.AddHealthChecks()
    .AddDbContextCheck<DocReaderDbContext>("postgres", tags: ["ready"])
    .AddCheck<PendingMigrationsHealthCheck>("migrations", tags: ["ready"])
    .AddCheck<StorageHealthCheck>("document-storage", tags: ["ready"])
    .AddCheck<OcrServiceHealthCheck>("ocr-service", tags: ["ready"]);

// ---------------------------------------------------------------------------------------------
// OpenAPI and Swagger UI (RF-015)
// ---------------------------------------------------------------------------------------------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "DocReader PoC API",
        Version = "v1",
        Description = """
            Local proof of concept for document ingestion, OCR, classification and extraction.

            **There is no authentication.** Every endpoint is anonymous, the Swagger page is public and
            no bearer token is required. Do not expose this API to the internet and do not send real
            personal documents to it. Use synthetic, masked or authorized files only.

            Conventions: JSON in camelCase, timestamps in UTC ISO 8601, errors as
            `application/problem+json`, and an `X-Correlation-Id` header on every response.

            Stage 2 of the execution plan delivers upload, listing, detail, content, deletion, OCR
            (PP-OCRv5 on CPU, see ADR 0002), the raw text and the structured result of the CPF card.
            Other document types are read as raw text only; their structured fields arrive in stage 3.
            """
    });

    foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "DocReader.*.xml"))
    {
        options.IncludeXmlComments(file, includeControllerXmlComments: true);
    }

    options.SupportNonNullableReferenceTypes();
    options.SchemaFilter<SuccessExamplesSchemaFilter>();
    options.OperationFilter<ResponseContentTypeOperationFilter>();
    options.OperationFilter<ProblemExamplesOperationFilter>();
    options.OperationFilter<DestructiveOperationFilter>();
});

var app = builder.Build();

// ---------------------------------------------------------------------------------------------
// Migrations are explicit: applied at start only when configured, never by EnsureCreated.
// ---------------------------------------------------------------------------------------------
var databaseOptions = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
if (databaseOptions.RunMigrationsOnStartup)
{
    await MigrateAsync(app, databaseOptions);
}

app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseCors();
app.UseMiddleware<AnonymousAccessGateMiddleware>();
app.UseMiddleware<UploadSizeGuardMiddleware>();

app.UseSwagger(options => options.RouteTemplate = "swagger/{documentName}/swagger.json");
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "DocReader PoC API v1");
    options.DocumentTitle = "DocReader PoC API";
    options.DisplayRequestDuration();
});

app.MapControllers();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthCheckResponseWriter.WriteAsync
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthCheckResponseWriter.WriteAsync
});

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

await app.RunAsync();

static async Task MigrateAsync(WebApplication app, DatabaseOptions options)
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();

    for (var attempt = 1; attempt <= Math.Max(1, options.MigrationAttempts); attempt++)
    {
        try
        {
            await using var scope = app.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DocReaderDbContext>();

            var pending = (await dbContext.Database.GetPendingMigrationsAsync()).ToArray();
            if (pending.Length == 0)
            {
                logger.LogInformation("Database schema is already up to date.");
                return;
            }

            logger.LogInformation("Applying {Count} pending migration(s).", pending.Length);
            await dbContext.Database.MigrateAsync();
            logger.LogInformation("Migrations applied.");
            return;
        }
        catch (Exception exception) when (attempt < options.MigrationAttempts)
        {
            logger.LogWarning(
                exception,
                "Migration attempt {Attempt} failed, retrying in {Delay}.",
                attempt,
                options.MigrationRetryDelay);

            await Task.Delay(options.MigrationRetryDelay);
        }
    }

    throw new InvalidOperationException("Could not apply database migrations.");
}
