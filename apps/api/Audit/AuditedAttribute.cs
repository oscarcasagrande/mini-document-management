using DocReader.Application.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace DocReader.Api.Audit;

/// <summary>
/// Opts a mutating action into the generic audit trail, instead of a bespoke <see cref="AuditLogService"/> call in
/// every service method. Records one <c>AuditLog</c> entry after a successful (2xx) response, through
/// <see cref="AuditDecision"/>: <c>resourceId</c> comes from the route value <c>id</c>/<c>jobId</c> when the
/// route carries one (every PUT/DELETE endpoint here does), otherwise from the <c>Id</c> property of the
/// response body (POST/create, whose id is only known once the handler runs). On a PUT, <c>changes</c> lists
/// the request body's property names that carry a value — never the value itself. A failed, rejected or
/// exception response is never recorded.
/// </summary>
/// <remarks>
/// Not applied to <c>StorageRepositoriesController.GetConnectionConfigAsync</c>: that endpoint is a GET (this
/// attribute only makes sense on a mutating action) and already records its own <c>STORAGE_CONFIG_REVEALED</c>
/// entry by hand.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AuditedAttribute(string action, string resourceType) : Attribute, IAsyncActionFilter
{
    public string Action { get; } = action;

    public string ResourceType { get; } = resourceType;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var routeValues = new Dictionary<string, object?>(context.RouteData.Values);
        var changes = HttpMethods.IsPut(context.HttpContext.Request.Method)
            ? AuditDecision.TouchedFields(AuditDecision.FindRequestBody(context.ActionArguments.Values))
            : null;

        var executed = await next();

        if (executed.Exception is not null && !executed.ExceptionHandled)
        {
            return;
        }

        if (!AuditDecision.IsSuccessStatusCode(StatusCodeOf(executed.Result)))
        {
            return;
        }

        var resourceId = AuditDecision.ResourceId(routeValues, ResponseBodyOf(executed.Result));
        if (resourceId is null)
        {
            return;
        }

        var auditLog = context.HttpContext.RequestServices.GetRequiredService<AuditLogService>();

        await auditLog.RecordAsync(
            context.HttpContext.User.Identity?.Name,
            Action,
            ResourceType,
            resourceId,
            context.HttpContext.Connection.RemoteIpAddress?.ToString(),
            context.HttpContext.Request.Headers.UserAgent.ToString(),
            changes,
            context.HttpContext.RequestAborted).ConfigureAwait(false);
    }

    private static int? StatusCodeOf(IActionResult? result) => result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? StatusCodes.Status200OK,
        StatusCodeResult statusCodeResult => statusCodeResult.StatusCode,
        _ => null
    };

    private static object? ResponseBodyOf(IActionResult? result) => (result as ObjectResult)?.Value;
}
