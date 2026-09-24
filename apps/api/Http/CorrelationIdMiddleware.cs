using Microsoft.Extensions.Primitives;

namespace DocReader.Api.Http;

/// <summary>
/// Puts a correlation id on every request and every response, and pushes it into the log scope so
/// each line of a request carries it.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers.TryGetValue(CorrelationId.HeaderName, out var header)
            ? header.ToString()
            : null;

        var correlationId = CorrelationId.Sanitize(incoming) ?? CorrelationId.Generate();

        context.Items[CorrelationId.ItemKey] = correlationId;
        context.Response.Headers[CorrelationId.HeaderName] = new StringValues(correlationId);

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["correlationId"] = correlationId
        });

        await next(context);
    }
}
