using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocReader.Api.Errors;
using DocReader.Api.HealthChecks;
using DocReader.Api.Http;
using DocReader.Api.Options;
using DocReader.Api.Security;
using DocReader.Api.Swagger;
using DocReader.Application;
using DocReader.Application.Options;
using DocReader.Infrastructure;
using DocReader.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

const string OAuthSecuritySchemeName = "OAuth2";

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

// Read directly from configuration, like uploadLimits and corsOrigins below: the decision to register
// JWT bearer auth at all has to be made before builder.Build(), while IOptions<T> is not resolvable yet.
var oidcOptions = builder.Configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>() ?? new OidcOptions();
var oidcConfigured = oidcOptions.IsConfigured;

builder.Services.AddOptions<OidcOptions>()
    .Bind(builder.Configuration.GetSection(OidcOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<OidcOptions>, OidcOptionsValidator>();

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
builder.Services.AddSingleton<ReadOnlyGate>();

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
// Authentication and authorization (ADR 0003). Anonymous mode (empty OIDC_AUTHORITY) registers no JWT
// bearer scheme at all: AnonymousAccessGateMiddleware keeps gating /api exactly as it always has, and
// AdminOnly below is an always-succeeding assertion, so the three admin controllers keep working with no
// token. Once OIDC_AUTHORITY is set, every controller requires an authenticated caller by default
// (FallbackPolicy) and the admin controllers additionally require the configured admin role; the two
// modes never overlap.
// ---------------------------------------------------------------------------------------------
if (oidcConfigured)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = oidcOptions.Authority;
            // Keeps original claim names ("sub", "email", "realm_access", ...) instead of the legacy
            // WS-Federation URIs the handler otherwise remaps them to.
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = !string.IsNullOrWhiteSpace(oidcOptions.ClientId),
                ValidAudience = oidcOptions.ClientId,
                NameClaimType = "name",
                RoleClaimType = ClaimTypes.Role
            };
            options.Events = new JwtBearerEvents
            {
                // Replaces the framework's default text/plain 401 body with problem+json (RFC 9457).
                OnChallenge = context =>
                {
                    context.HandleResponse();
                    return AuthProblemWriter.WriteUnauthenticatedAsync(context.HttpContext);
                },
                OnForbidden = context => AuthProblemWriter.WriteForbiddenAsync(context.HttpContext)
            };
        });

    // Keycloak (and most realm-based IdPs) nest roles under realm_access.roles, which the handler above
    // does not understand as role claims on its own.
    builder.Services.AddTransient<IClaimsTransformation, KeycloakRealmRolesClaimsTransformation>();
}

builder.Services.AddAuthorization(options =>
{
    if (oidcConfigured)
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    }

    AuthorizationPolicies.ConfigureAdminOnly(options, oidcConfigured, oidcOptions.AdminRole);
});

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
var authDescription = oidcConfigured
    ? """
        **Authentication:** every endpoint (health checks excepted) requires a bearer token issued by
        the configured OIDC provider. The three `/admin` endpoints (backup, restore, storage migration)
        additionally require the `docreader-admin` role. Click **Authorize** above to sign in
        interactively; see ADR 0003.
        """
    : """
        **There is no authentication.** Every endpoint is anonymous, the Swagger page is public and
        no bearer token is required. Do not expose this API to the internet and do not send real
        personal documents to it. Use synthetic, masked or authorized files only.
        """;

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "DocReader PoC API",
        Version = "v1",
        Description = $"""
            Local proof of concept for document ingestion, OCR, classification and extraction.

            {authDescription}

            Conventions: JSON in camelCase, timestamps in UTC ISO 8601, errors as
            `application/problem+json`, and an `X-Correlation-Id` header on every response.

            Stage 3 of the execution plan delivers upload, listing, detail, content, deletion, OCR
            (PP-OCRv5 on CPU, see ADR 0002), the raw text and the structured result of seven document
            types: CPF card, CIN/RG, CNH, proof of address, CNPJ card, CCMEI and social contract.
            Other document types are read as raw text only.
            """
    });

    if (oidcConfigured)
    {
        var authorizationEndpoint = new Uri($"{oidcOptions.Authority!.TrimEnd('/')}/protocol/openid-connect/auth");
        var tokenEndpoint = new Uri($"{oidcOptions.Authority!.TrimEnd('/')}/protocol/openid-connect/token");

        options.AddSecurityDefinition(OAuthSecuritySchemeName, new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Description =
                "Authorization code + PKCE against the configured OIDC provider, for interactive testing " +
                "in this page. The API itself only ever validates the resulting bearer token; it never " +
                "redirects or issues one (ADR 0003). Endpoint paths follow the Keycloak convention " +
                "{authority}/protocol/openid-connect/{auth,token}.",
            Flows = new OpenApiOAuthFlows
            {
                AuthorizationCode = new OpenApiOAuthFlow
                {
                    AuthorizationUrl = authorizationEndpoint,
                    TokenUrl = tokenEndpoint,
                    Scopes = new Dictionary<string, string>
                    {
                        ["openid"] = "OpenID Connect sign-in",
                        ["profile"] = "Basic profile",
                        ["email"] = "Email address"
                    }
                }
            }
        });

        options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(OAuthSecuritySchemeName, document)] = ["openid", "profile", "email"]
        });
    }

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

if (oidcConfigured)
{
    app.UseAuthentication();
}

app.UseAuthorization();

app.UseMiddleware<ReadOnlyGateMiddleware>();
app.UseMiddleware<UploadSizeGuardMiddleware>();

app.UseSwagger(options => options.RouteTemplate = "swagger/{documentName}/swagger.json");
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "DocReader PoC API v1");
    options.DocumentTitle = "DocReader PoC API";
    options.DisplayRequestDuration();

    if (oidcConfigured)
    {
        options.OAuthClientId(oidcOptions.ClientId);
        options.OAuthUsePkce();
    }
});

app.MapControllers();

// FallbackPolicy (when OIDC is configured) applies to every endpoint routed through MapControllers or
// Map*, so health checks and the root redirect are excluded explicitly: they must stay reachable without
// a token no matter which mode is active.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthCheckResponseWriter.WriteAsync
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthCheckResponseWriter.WriteAsync
}).AllowAnonymous();

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription().AllowAnonymous();

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
