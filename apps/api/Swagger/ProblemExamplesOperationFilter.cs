using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace DocReader.Api.Swagger;

/// <summary>
/// Fills every documented error response with a concrete <c>application/problem+json</c> example, so
/// the Swagger page shows what a failure actually looks like (Swagger DoD, PRD section 17).
/// </summary>
public sealed class ProblemExamplesOperationFilter : IOperationFilter
{
    private static readonly Dictionary<string, (string Title, string Type, string ErrorCode, string Detail)> Examples =
        new(StringComparer.Ordinal)
        {
            ["400"] = ("One or more validation errors occurred", "validation-failed", "VALIDATION_FAILED",
                "Check the errors property for the fields that were refused."),
            ["404"] = ("Document not found", "document-not-found", "DOCUMENT_NOT_FOUND",
                "No document matches the requested identifier."),
            ["409"] = ("Idempotency-Key conflict", "conflict", "IDEMPOTENCY_KEY_CONFLICT",
                "This Idempotency-Key was already used with a different file. Use a new key or resend the original file."),
            ["410"] = ("Original file is no longer available", "document-content-unavailable",
                "DOCUMENT_CONTENT_MISSING",
                "The document metadata exists but the stored file is gone."),
            ["413"] = ("File too large", "file-too-large", "FILE_TOO_LARGE",
                "The uploaded file has 41943040 bytes and the limit is 26214400 bytes."),
            ["415"] = ("Unsupported media type", "unsupported-media-type", "UNSUPPORTED_MEDIA_TYPE",
                "Only PDF, PNG, JPEG and TIFF files are accepted."),
            ["422"] = ("Content cannot be processed", "unprocessable-content", "PAGE_LIMIT_EXCEEDED",
                "The document has 84 pages and the limit is 50 pages."),
            ["500"] = ("Unexpected error", "unexpected-error", "UNEXPECTED_ERROR",
                "The request could not be completed. Check the API logs using the correlation id of this response."),
            ["503"] = ("Anonymous access is disabled", "anonymous-access-disabled", "ANONYMOUS_ACCESS_DISABLED",
                "Requests to /api/ are refused because ALLOW_ANONYMOUS_ACCESS is false.")
        };

    /// <summary>
    /// The same status code means different things on different operations: a 409 is an Idempotency-Key
    /// conflict on upload, a missing result on text and result, and a concurrent run on reprocess.
    /// </summary>
    private static readonly (string PathSuffix, string StatusCode, (string Title, string Type, string ErrorCode, string Detail) Example)[] Overrides =
    [
        ("/retention-policies", "409", ("Conflict", "conflict", "RETENTION_POLICY_EXISTS",
            "A retention policy for this document type and product or service already exists. Change it instead of creating another.")),
        ("/retention-policies", "422", ("Content cannot be processed", "unprocessable-content", "PRODUCT_SERVICE_NOT_FOUND",
            "There is no product or service with the id 0199c1f0-1111-7a10-9c44-2f1d8e6b4a21.")),
        ("/retention-policies/{id}", "409", ("Conflict", "conflict", "GLOBAL_RETENTION_POLICY_PROTECTED",
            "The global retention policy cannot be deleted: it is what applies to a document no other policy covers. Change its retentionDays instead.")),
        ("/retention-policies/{id}", "404", ("Resource not found", "document-not-found", "RETENTION_POLICY_NOT_FOUND",
            "No retention policy matches the requested identifier.")),
        ("/webhook-subscriptions", "422", ("Content cannot be processed", "unprocessable-content", "PRODUCT_SERVICE_NOT_FOUND",
            "There is no product or service with the id 0199c1f0-1111-7a10-9c44-2f1d8e6b4a21.")),
        ("/webhook-subscriptions/{id}", "404", ("Resource not found", "document-not-found", "WEBHOOK_SUBSCRIPTION_NOT_FOUND",
            "No webhook subscription matches the requested identifier.")),
        ("/webhook-subscriptions/{id}/deliveries", "404", ("Resource not found", "document-not-found", "WEBHOOK_SUBSCRIPTION_NOT_FOUND",
            "No webhook subscription matches the requested identifier.")),
        ("/storage-repositories", "409", ("Conflict", "conflict", "STORAGE_REPOSITORY_CODE_EXISTS",
            "A storage repository with the code ARQUIVO-DB already exists.")),
        ("/storage-repositories", "422", ("Content cannot be processed", "unprocessable-content", "STORAGE_PROVIDER_NOT_IMPLEMENTED",
            "The AwsS3 provider is registered but its adapter is not implemented yet, so it cannot be the default repository.")),
        ("/storage-repositories/{id}", "409", ("Conflict", "conflict", "DEFAULT_STORAGE_REPOSITORY_REQUIRED",
            "The default storage repository cannot be deleted. Make another repository the default first.")),
        ("/storage-repositories/{id}", "404", ("Resource not found", "document-not-found", "STORAGE_REPOSITORY_NOT_FOUND",
            "No storage repository matches the requested identifier.")),
        ("/product-services", "409", ("Conflict", "conflict", "PRODUCT_SERVICE_CODE_EXISTS",
            "A product or service with the code CONTA-PJ already exists.")),
        ("/product-services/{id}", "409", ("Conflict", "conflict", "PRODUCT_SERVICE_IN_USE",
            "Documents, retention policies or webhook subscriptions still refer to this product or service. Deactivate it (active = false) instead of deleting it.")),
        ("/product-services/{id}", "404", ("Resource not found", "document-not-found", "PRODUCT_SERVICE_NOT_FOUND",
            "No product service matches the requested identifier.")),
        ("/text", "409", ("Result not available", "conflict", "RESULT_NOT_READY",
            "The document is OCR_RUNNING and has no result yet. Poll the status endpoint and try again when it is COMPLETED.")),
        ("/extraction-diagnostics", "409", ("Result not available", "conflict", "RESULT_NOT_READY",
            "The document is OCR_RUNNING and has no result yet. Poll the status endpoint and try again when it is COMPLETED.")),
        ("/classification-diagnostics", "409", ("Result not available", "conflict", "RESULT_NOT_READY",
            "The document is OCR_RUNNING and has no result yet. Poll the status endpoint and try again when it is COMPLETED.")),
        ("/result", "409", ("Result not available", "conflict", "RESULT_NOT_READY",
            "The document is OCR_RUNNING and has no result yet. Poll the status endpoint and try again when it is COMPLETED.")),
        ("/content", "410", ("Document was purged", "document-content-unavailable", "DOCUMENT_PURGED",
            "The retention period of this document ended: its original file, the text read by OCR and the extracted fields were removed. The record, the metadata and the history are kept.")),
        ("/reprocess", "410", ("Document was purged", "document-content-unavailable", "DOCUMENT_PURGED",
            "The retention period of this document ended: its original file, the text read by OCR and the extracted fields were removed. The record, the metadata and the history are kept.")),
        ("/text", "410", ("Document was purged", "document-content-unavailable", "DOCUMENT_PURGED",
            "The retention period of this document ended: its original file, the text read by OCR and the extracted fields were removed. The record, the metadata and the history are kept.")),
        ("/result", "410", ("Document was purged", "document-content-unavailable", "DOCUMENT_PURGED",
            "The retention period of this document ended: its original file, the text read by OCR and the extracted fields were removed. The record, the metadata and the history are kept.")),
        ("/classification-diagnostics", "410", ("Document was purged", "document-content-unavailable", "DOCUMENT_PURGED",
            "The retention period of this document ended: its original file, the text read by OCR and the extracted fields were removed. The record, the metadata and the history are kept.")),
        ("/extraction-diagnostics", "410", ("Document was purged", "document-content-unavailable", "DOCUMENT_PURGED",
            "The retention period of this document ended: its original file, the text read by OCR and the extracted fields were removed. The record, the metadata and the history are kept.")),
        ("/reprocess", "409", ("Document already being processed", "conflict", "REPROCESS_CONFLICT",
            "The document is queued or being processed, or it was rejected. Wait for it to finish before reprocessing."))
    ];

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath is null ? "/" : "/" + context.ApiDescription.RelativePath;

        if (operation.Responses is null)
        {
            return;
        }

        foreach (var (statusCode, response) in operation.Responses)
        {
            if (!Examples.TryGetValue(statusCode, out var example) || response.Content is null)
            {
                continue;
            }

            foreach (var (suffix, overrideStatus, overrideExample) in Overrides)
            {
                if (overrideStatus == statusCode && path.EndsWith(suffix, StringComparison.Ordinal))
                {
                    example = overrideExample;
                }
            }

            var body = new JsonObject
            {
                ["type"] = $"https://docreader.local/problems/{example.Type}",
                ["title"] = example.Title,
                ["status"] = int.Parse(statusCode, System.Globalization.CultureInfo.InvariantCulture),
                ["detail"] = example.Detail,
                ["instance"] = path,
                ["errorCode"] = example.ErrorCode,
                ["correlationId"] = "9f1c1d0f2a5b4c8e9d7a6b5c4d3e2f10"
            };

            foreach (var content in response.Content)
            {
                content.Value.Example = body.DeepClone();
            }
        }
    }
}
