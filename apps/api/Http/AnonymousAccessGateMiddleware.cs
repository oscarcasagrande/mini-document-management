using DocReader.Api.Errors;
using DocReader.Api.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;

namespace DocReader.Api.Http;

/// <summary>
/// Enforces <c>ALLOW_ANONYMOUS_ACCESS</c>. The PoC ships no authentication provider, so turning
/// anonymous access off closes the API instead of asking for credentials that nothing can issue.
/// Health endpoints stay reachable so an operator can still see why.
/// </summary>
/// <remarks>
/// Steps aside entirely once <c>OIDC_AUTHORITY</c> is configured (ADR 0003): the JWT bearer scheme's own
/// 401 challenge is the real gate then, and <c>ALLOW_ANONYMOUS_ACCESS</c> no longer has a meaning to
/// enforce here. The two gates never overlap by design, so there is only one code path to reason about
/// for a given deployment.
/// </remarks>
public sealed class AnonymousAccessGateMiddleware(
    RequestDelegate next,
    IOptions<SecurityOptions> options,
    IOptions<OidcOptions> oidcOptions,
    ProblemDetailsFactory problemDetailsFactory)
{
    private const string GuardedPathPrefix = "/api/";

    public async Task InvokeAsync(HttpContext context)
    {
        if (oidcOptions.Value.IsConfigured ||
            options.Value.AllowAnonymousAccess ||
            !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var problem = problemDetailsFactory.CreateProblemDetails(
            context,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Anonymous access is disabled",
            type: ProblemTypes.Build("anonymous-access-disabled"),
            detail: $"Requests to {GuardedPathPrefix} are refused because ALLOW_ANONYMOUS_ACCESS is false and this proof of concept ships no authentication provider.");

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = ProblemTypes.ContentType;

        await context.Response.WriteAsJsonAsync(
            problem,
            problem.GetType(),
            (System.Text.Json.JsonSerializerOptions?)null,
            ProblemTypes.ContentType,
            context.RequestAborted);
    }
}
