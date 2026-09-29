namespace DocReader.Application.Audit;

/// <summary>
/// Pure decision logic behind the API's generic audit filter (<c>AuditedAttribute</c>, in
/// <c>DocReader.Api</c>): whether a response should be recorded, which resource id to record it under, and
/// which request field names were touched. It takes plain values only — no <c>HttpContext</c>, no MVC filter
/// types — so it is unit tested directly, without standing up the ASP.NET Core pipeline.
/// </summary>
public static class AuditDecision
{
    private static readonly string[] DefaultRouteIdKeys = ["id", "jobId"];

    /// <summary>Only a successful response is recorded: a failed or rejected request never happened as far as the audit trail is concerned.</summary>
    public static bool IsSuccessStatusCode(int? statusCode) => statusCode is >= 200 and < 300;

    /// <summary>
    /// The affected resource's id: the route value named <c>id</c> or <c>jobId</c> when present (covers the
    /// PUT/DELETE routes, all addressed by id), otherwise the <c>Id</c> property of the response body (covers
    /// POST/create, whose id is only known once the handler runs). Null when neither is available.
    /// </summary>
    public static string? ResourceId(IReadOnlyDictionary<string, object?> routeValues, object? responseBody) =>
        ResourceIdFromRoute(routeValues, DefaultRouteIdKeys) ?? IdOf(responseBody);

    public static string? ResourceIdFromRoute(IReadOnlyDictionary<string, object?> routeValues, IReadOnlyList<string> routeIdKeys)
    {
        foreach (var key in routeIdKeys)
        {
            if (routeValues.TryGetValue(key, out var value) && value is not null)
            {
                return value.ToString();
            }
        }

        return null;
    }

    /// <summary>The <c>Id</c> property of an object, read by reflection; every response contract audited here starts with one.</summary>
    public static string? IdOf(object? value)
    {
        var id = value?.GetType().GetProperty("Id")?.GetValue(value);

        return id switch
        {
            null => null,
            Guid guid => guid.ToString(),
            string text => string.IsNullOrWhiteSpace(text) ? null : text,
            _ => id.ToString()
        };
    }

    /// <summary>The first action argument that looks like a request body: a class, not a route id and not the cancellation token.</summary>
    public static object? FindRequestBody(IEnumerable<object?> actionArguments) =>
        actionArguments.FirstOrDefault(IsRequestBodyCandidate);

    /// <summary>
    /// Names of the request body's properties that carry a value, comma separated. Never the value itself: this
    /// is metadata about what an update touched, per the "no document content, no secret value" logging rule.
    /// Null when there is no request body or it touches nothing.
    /// </summary>
    public static string? TouchedFields(object? requestBody)
    {
        if (requestBody is null)
        {
            return null;
        }

        var touched = requestBody.GetType()
            .GetProperties()
            .Where(property => property.CanRead && !string.Equals(property.Name, "Id", StringComparison.Ordinal))
            .Where(property => IsPresent(property.GetValue(requestBody)))
            .Select(property => property.Name)
            .ToArray();

        return touched.Length == 0 ? null : string.Join(",", touched);
    }

    private static bool IsRequestBodyCandidate(object? value) =>
        value is not null
        && value is not CancellationToken
        && value is not Guid
        && value is not string
        && value.GetType().IsClass;

    private static bool IsPresent(object? value) => value switch
    {
        null => false,
        string text => text.Length > 0,
        _ => true
    };
}
