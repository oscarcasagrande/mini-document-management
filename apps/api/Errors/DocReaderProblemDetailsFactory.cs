using System.Diagnostics;
using DocReader.Api.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DocReader.Api.Errors;

/// <summary>
/// Single shape for every error of the API, including the automatic model validation responses:
/// RFC 9457 <c>application/problem+json</c> carrying the correlation id of the response.
/// </summary>
public sealed class DocReaderProblemDetailsFactory : ProblemDetailsFactory
{
    public override ProblemDetails CreateProblemDetails(
        HttpContext httpContext,
        int? statusCode = null,
        string? title = null,
        string? type = null,
        string? detail = null,
        string? instance = null)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode ?? StatusCodes.Status500InternalServerError,
            Title = title ?? "Unexpected error",
            Type = type ?? ProblemTypes.Build(ProblemTypes.Unexpected),
            Detail = detail,
            Instance = instance ?? httpContext.Request.Path.Value
        };

        Enrich(httpContext, problem);

        return problem;
    }

    public override ValidationProblemDetails CreateValidationProblemDetails(
        HttpContext httpContext,
        ModelStateDictionary modelStateDictionary,
        int? statusCode = null,
        string? title = null,
        string? type = null,
        string? detail = null,
        string? instance = null)
    {
        ArgumentNullException.ThrowIfNull(modelStateDictionary);

        var problem = new ValidationProblemDetails(modelStateDictionary)
        {
            Status = statusCode ?? StatusCodes.Status400BadRequest,
            Title = title ?? "One or more validation errors occurred",
            Type = type ?? ProblemTypes.Build(ProblemTypes.Validation),
            Detail = detail ?? "Check the errors property for the fields that were refused.",
            Instance = instance ?? httpContext.Request.Path.Value
        };

        problem.Extensions["errorCode"] = "VALIDATION_FAILED";
        Enrich(httpContext, problem);

        return problem;
    }

    private static void Enrich(HttpContext httpContext, ProblemDetails problem)
    {
        if (httpContext.Items.TryGetValue(CorrelationId.ItemKey, out var stored) && stored is string correlationId)
        {
            problem.Extensions["correlationId"] = correlationId;
        }

        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        problem.Extensions["traceId"] = traceId;
    }
}
