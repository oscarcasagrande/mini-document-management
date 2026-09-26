using DocReader.Api.Http;
using DocReader.Application.Errors;
using DocReader.Domain;
using DocReader.Domain.Documents;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace DocReader.Api.Errors;

/// <summary>
/// Turns every unhandled exception into <c>application/problem+json</c>. No stack trace and no
/// document content ever reach the client or the log message.
/// </summary>
public sealed class ApiExceptionHandler(
    ProblemDetailsFactory problemDetailsFactory,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var descriptor = Describe(exception);

        if (descriptor.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(
                exception,
                "Unhandled error while handling {Method} {Path}. errorCode={ErrorCode}",
                httpContext.Request.Method,
                httpContext.Request.Path.Value,
                descriptor.ErrorCode);
        }
        else
        {
            logger.LogWarning(
                "Request refused. {Method} {Path} status={Status} errorCode={ErrorCode}",
                httpContext.Request.Method,
                httpContext.Request.Path.Value,
                descriptor.StatusCode,
                descriptor.ErrorCode);
        }

        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        var problem = problemDetailsFactory.CreateProblemDetails(
            httpContext,
            statusCode: descriptor.StatusCode,
            title: descriptor.Title,
            type: ProblemTypes.Build(descriptor.ProblemType),
            detail: descriptor.Detail);

        problem.Extensions["errorCode"] = descriptor.ErrorCode;

        httpContext.Response.StatusCode = descriptor.StatusCode;
        httpContext.Response.ContentType = ProblemTypes.ContentType;

        // The exception handler pipeline clears headers, so the correlation id is written again.
        if (httpContext.Items.TryGetValue(CorrelationId.ItemKey, out var correlationId) &&
            correlationId is string value)
        {
            httpContext.Response.Headers[CorrelationId.HeaderName] = value;
        }

        await httpContext.Response
            .WriteAsJsonAsync(problem, problem.GetType(), (System.Text.Json.JsonSerializerOptions?)null, ProblemTypes.ContentType, cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    private static ErrorDescriptor Describe(Exception exception) => exception switch
    {
        UploadRejectedException rejected => DescribeUploadRejection(rejected),

        IdempotencyConflictException => new ErrorDescriptor(
            StatusCodes.Status409Conflict,
            "Idempotency-Key conflict",
            ProblemTypes.Conflict,
            "IDEMPOTENCY_KEY_CONFLICT",
            "This Idempotency-Key was already used with a different file. Use a new key or resend the original file."),

        InvalidFilterException filter => new ErrorDescriptor(
            StatusCodes.Status400BadRequest,
            "Invalid filter",
            ProblemTypes.Validation,
            "INVALID_FILTER",
            filter.Message),

        ResourceNotFoundException notFound => new ErrorDescriptor(
            StatusCodes.Status404NotFound,
            "Resource not found",
            ProblemTypes.NotFound,
            notFound.Resource.ToUpperInvariant().Replace('-', '_') + "_NOT_FOUND",
            $"No {notFound.Resource.Replace('-', ' ')} matches the requested identifier."),

        ResourceConflictException conflict => new ErrorDescriptor(
            StatusCodes.Status409Conflict,
            "Conflict",
            ProblemTypes.Conflict,
            conflict.ErrorCode,
            conflict.Message),

        NotImplementedException => new ErrorDescriptor(
            StatusCodes.Status501NotImplemented,
            "Storage provider not implemented",
            ProblemTypes.Unexpected,
            "STORAGE_PROVIDER_NOT_IMPLEMENTED",
            "The storage repository uses a provider whose adapter is not implemented yet (Azure Blob Storage or AWS S3). Use a FILE_SYSTEM or DATABASE repository."),

        UnprocessableRequestException unprocessable => new ErrorDescriptor(
            StatusCodes.Status422UnprocessableEntity,
            "Content cannot be processed",
            ProblemTypes.UnprocessableContent,
            unprocessable.ErrorCode,
            unprocessable.Message),

        DocumentPurgedException => new ErrorDescriptor(
            StatusCodes.Status410Gone,
            "Document was purged",
            ProblemTypes.ContentUnavailable,
            "DOCUMENT_PURGED",
            "The retention period of this document ended: its original file, the text read by OCR and the extracted fields were removed. The record, the metadata and the history are kept."),

        RequestValidationException invalid => new ErrorDescriptor(
            StatusCodes.Status400BadRequest,
            "Invalid request",
            ProblemTypes.Validation,
            invalid.ErrorCode,
            invalid.Message),

        DocumentNotFoundException => new ErrorDescriptor(
            StatusCodes.Status404NotFound,
            "Document not found",
            ProblemTypes.NotFound,
            "DOCUMENT_NOT_FOUND",
            "No document matches the requested identifier."),

        ResultNotReadyException notReady => new ErrorDescriptor(
            StatusCodes.Status409Conflict,
            "Result not available",
            ProblemTypes.Conflict,
            "RESULT_NOT_READY",
            notReady.Status is DocumentStatus.Failed
                ? "Processing failed and there is no earlier result to return. Check lastError on the document and reprocess it."
                : $"The document is {EnumNaming.ToUpperSnakeCase(notReady.Status)} and has no result yet. Poll the status endpoint and try again when it is COMPLETED."),

        ReprocessConflictException => new ErrorDescriptor(
            StatusCodes.Status409Conflict,
            "Document already being processed",
            ProblemTypes.Conflict,
            "REPROCESS_CONFLICT",
            "The document is queued or being processed, or it was rejected. Wait for it to finish before reprocessing."),

        DocumentContentMissingException => new ErrorDescriptor(
            StatusCodes.Status410Gone,
            "Original file is no longer available",
            ProblemTypes.ContentUnavailable,
            "DOCUMENT_CONTENT_MISSING",
            "The document metadata exists but the stored file is gone. The storage volume was probably removed without the database."),

        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } => new ErrorDescriptor(
            StatusCodes.Status413PayloadTooLarge,
            "Request body too large",
            ProblemTypes.PayloadTooLarge,
            "REQUEST_TOO_LARGE",
            "The request body exceeds the configured limit and was rejected before being read."),

        BadHttpRequestException badRequest => new ErrorDescriptor(
            badRequest.StatusCode,
            "Malformed request",
            ProblemTypes.Validation,
            "MALFORMED_REQUEST",
            "The request could not be read. Check the multipart body and the headers."),

        OperationCanceledException => new ErrorDescriptor(
            StatusCodes.Status499ClientClosedRequest,
            "Request cancelled",
            ProblemTypes.Validation,
            "REQUEST_CANCELLED",
            "The client closed the connection before the request finished."),

        _ => new ErrorDescriptor(
            StatusCodes.Status500InternalServerError,
            "Unexpected error",
            ProblemTypes.Unexpected,
            "UNEXPECTED_ERROR",
            "The request could not be completed. Check the API logs using the correlation id of this response.")
    };

    private static ErrorDescriptor DescribeUploadRejection(UploadRejectedException exception) =>
        exception.Reason switch
        {
            UploadRejectionReason.TooLarge => new ErrorDescriptor(
                StatusCodes.Status413PayloadTooLarge,
                "File too large",
                ProblemTypes.PayloadTooLarge,
                exception.ErrorCode,
                exception.Message),

            UploadRejectionReason.UnsupportedFormat or
                UploadRejectionReason.DeclaredTypeMismatch or
                UploadRejectionReason.BlockedContent => new ErrorDescriptor(
                    StatusCodes.Status415UnsupportedMediaType,
                    "Unsupported media type",
                    ProblemTypes.UnsupportedMediaType,
                    exception.ErrorCode,
                    exception.Message),

            UploadRejectionReason.UnprocessableContent => new ErrorDescriptor(
                StatusCodes.Status422UnprocessableEntity,
                "Content cannot be processed",
                ProblemTypes.UnprocessableContent,
                exception.ErrorCode,
                exception.Message),

            _ => new ErrorDescriptor(
                StatusCodes.Status400BadRequest,
                "Invalid request",
                ProblemTypes.Validation,
                exception.ErrorCode,
                exception.Message)
        };

    private sealed record ErrorDescriptor(
        int StatusCode,
        string Title,
        string ProblemType,
        string ErrorCode,
        string Detail);
}
