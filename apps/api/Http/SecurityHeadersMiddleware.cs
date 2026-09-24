namespace DocReader.Api.Http;

/// <summary>
/// Basic response headers required by PRD section 21. Deliberately narrow: nothing here may break
/// inline rendering of a PDF in a browser tab or the Swagger UI.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Permitted-Cross-Domain-Policies"] = "none";

        return next(context);
    }
}
