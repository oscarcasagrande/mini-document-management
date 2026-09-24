using System.Globalization;
using DocReader.Api.Errors;
using DocReader.Application.Options;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;

namespace DocReader.Api.Http;

/// <summary>
/// Refuses an oversized upload from its <c>Content-Length</c>, before the multipart reader touches the
/// body. Without this, a body above the transport limit surfaces as a form binding failure and the
/// client would get a 400 where the contract promises a 413 (PRD section 16).
/// </summary>
public sealed class UploadSizeGuardMiddleware(
    RequestDelegate next,
    IOptions<UploadOptions> uploadOptions,
    ProblemDetailsFactory problemDetailsFactory,
    ILogger<UploadSizeGuardMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var maxSizeBytes = uploadOptions.Value.MaxSizeBytes;
        var declaredLength = context.Request.ContentLength;

        var isUpload = HttpMethods.IsPost(context.Request.Method) &&
                       context.Request.Path.StartsWithSegments("/api/v1/documents", StringComparison.OrdinalIgnoreCase);

        if (!isUpload || declaredLength is null || declaredLength <= maxSizeBytes)
        {
            await next(context);
            return;
        }

        logger.LogWarning(
            "Upload refused before reading the body. contentLength={ContentLength} maxSizeBytes={MaxSizeBytes}",
            declaredLength,
            maxSizeBytes);

        var problem = problemDetailsFactory.CreateProblemDetails(
            context,
            statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "File too large",
            type: ProblemTypes.Build(ProblemTypes.PayloadTooLarge),
            detail: string.Create(
                CultureInfo.InvariantCulture,
                $"The request body has {declaredLength} bytes and the limit is {maxSizeBytes} bytes."));

        problem.Extensions["errorCode"] = "FILE_TOO_LARGE";

        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        context.Response.ContentType = ProblemTypes.ContentType;

        await context.Response.WriteAsJsonAsync(
            problem,
            problem.GetType(),
            (System.Text.Json.JsonSerializerOptions?)null,
            ProblemTypes.ContentType,
            context.RequestAborted);
    }
}
