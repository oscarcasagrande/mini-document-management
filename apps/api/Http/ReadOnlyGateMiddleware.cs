using DocReader.Api.Errors;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace DocReader.Api.Http;

/// <summary>
/// While a restore replaces the database, refuses every request that could write (POST, PUT, PATCH, DELETE) with 503 and
/// <c>SYSTEM_READ_ONLY</c>. Reads keep working. The restore endpoint itself is let through (it answers 409 on its own if a
/// restore is already running), and so is everything outside <c>/api</c>, health checks included.
/// </summary>
public sealed class ReadOnlyGateMiddleware(
    RequestDelegate next,
    ReadOnlyGate gate,
    ProblemDetailsFactory problemDetailsFactory,
    ILogger<ReadOnlyGateMiddleware> logger)
{
    public const string RestorePath = "/api/v1/admin/restore";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsGuarded(context.Request) || !await gate.IsReadOnlyAsync(context.RequestAborted))
        {
            await next(context);
            return;
        }

        logger.LogWarning(
            "Request refused while the system is read-only. {Method} {Path}",
            context.Request.Method,
            context.Request.Path.Value);

        var problem = problemDetailsFactory.CreateProblemDetails(
            context,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "System is read-only",
            type: ProblemTypes.Build(ProblemTypes.SystemReadOnly),
            detail: "A restore is replacing the database. Reads keep working; retry changes once the restore job has finished.");

        problem.Extensions["errorCode"] = "SYSTEM_READ_ONLY";

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = ProblemTypes.ContentType;
        context.Response.Headers.RetryAfter = "30";

        await context.Response.WriteAsJsonAsync(
            problem,
            problem.GetType(),
            (System.Text.Json.JsonSerializerOptions?)null,
            ProblemTypes.ContentType,
            context.RequestAborted);
    }

    public static bool IsGuarded(HttpRequest request)
    {
        var mutating = HttpMethods.IsPost(request.Method) ||
                       HttpMethods.IsPut(request.Method) ||
                       HttpMethods.IsPatch(request.Method) ||
                       HttpMethods.IsDelete(request.Method);

        return mutating &&
               request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
               !request.Path.StartsWithSegments(RestorePath, StringComparison.OrdinalIgnoreCase);
    }
}
