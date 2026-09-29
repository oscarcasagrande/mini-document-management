using DocReader.Api.Errors;
using DocReader.Api.Http;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace DocReader.Api.Security;

/// <summary>
/// Writes the <c>application/problem+json</c> body for a missing or rejected bearer token, in the same
/// shape as <see cref="ApiExceptionHandler"/>. Used from <c>JwtBearerEvents.OnChallenge</c> (401, no or
/// invalid token) and <c>OnForbidden</c> (403, valid token but missing the required role), so neither
/// leaks the framework's default plain text <c>WWW-Authenticate</c> challenge body.
/// </summary>
public static class AuthProblemWriter
{
    public static Task WriteUnauthenticatedAsync(HttpContext httpContext) => WriteAsync(
        httpContext,
        StatusCodes.Status401Unauthorized,
        "Authentication required",
        ProblemTypes.Unauthenticated,
        "UNAUTHENTICATED",
        "This request requires a valid bearer token issued by the configured OIDC provider (OIDC_AUTHORITY).");

    public static Task WriteForbiddenAsync(HttpContext httpContext) => WriteAsync(
        httpContext,
        StatusCodes.Status403Forbidden,
        "Insufficient role",
        ProblemTypes.Forbidden,
        "FORBIDDEN",
        "The authenticated user does not have the role this endpoint requires.");

    private static async Task WriteAsync(
        HttpContext httpContext,
        int statusCode,
        string title,
        string typeSlug,
        string errorCode,
        string detail)
    {
        var problemDetailsFactory = httpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();

        var problem = problemDetailsFactory.CreateProblemDetails(
            httpContext,
            statusCode: statusCode,
            title: title,
            type: ProblemTypes.Build(typeSlug),
            detail: detail);

        problem.Extensions["errorCode"] = errorCode;

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = ProblemTypes.ContentType;

        if (httpContext.Items.TryGetValue(CorrelationId.ItemKey, out var correlationId) &&
            correlationId is string value)
        {
            httpContext.Response.Headers[CorrelationId.HeaderName] = value;
        }

        await httpContext.Response.WriteAsJsonAsync(
            problem,
            problem.GetType(),
            (System.Text.Json.JsonSerializerOptions?)null,
            ProblemTypes.ContentType,
            httpContext.RequestAborted);
    }
}
