using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DocReader.Api.Errors;

/// <summary>
/// Shapes the automatic model validation answer: always <c>application/problem+json</c>, and a body
/// that could not be read because it was too large is reported as 413 rather than as a field error.
/// </summary>
public static class InvalidModelStateResponse
{
    public static IActionResult Create(ActionContext context)
    {
        var factory = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();

        if (TryFindBodyTooLarge(context.ModelState))
        {
            var tooLarge = factory.CreateProblemDetails(
                context.HttpContext,
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "Request body too large",
                type: ProblemTypes.Build(ProblemTypes.PayloadTooLarge),
                detail: "The request body exceeds the configured limit and was refused while being read.");

            tooLarge.Extensions["errorCode"] = "REQUEST_TOO_LARGE";

            return new ObjectResult(tooLarge)
            {
                StatusCode = StatusCodes.Status413PayloadTooLarge,
                ContentTypes = { ProblemTypes.ContentType }
            };
        }

        var problem = factory.CreateValidationProblemDetails(
            context.HttpContext,
            context.ModelState,
            statusCode: StatusCodes.Status400BadRequest);

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { ProblemTypes.ContentType }
        };
    }

    /// <summary>
    /// A body cut off by the transport limit reaches model binding as an exception attached to the
    /// model state, not as a validation message.
    /// </summary>
    private static bool TryFindBodyTooLarge(ModelStateDictionary modelState)
    {
        foreach (var entry in modelState)
        {
            foreach (var error in entry.Value.Errors)
            {
                if (IsBodyTooLarge(error.Exception))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsBodyTooLarge(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
            {
                return true;
            }

            if (current is InvalidDataException)
            {
                return true;
            }
        }

        return false;
    }
}
