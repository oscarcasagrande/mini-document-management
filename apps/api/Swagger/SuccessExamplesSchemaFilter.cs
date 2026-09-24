using System.Text.Json.Nodes;
using DocReader.Api.Contracts.V1;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace DocReader.Api.Swagger;

/// <summary>
/// Attaches a realistic success example to each public response schema. Values mirror the canonical
/// result of PRD section 15.
/// </summary>
public sealed class SuccessExamplesSchemaFilter : ISchemaFilter
{
    private const string SampleId = "0199c1f0-7b3a-7a10-9c44-2f1d8e6b4a21";
    private const string SampleProtocol = "DOC-20260924-000001";
    private const string SampleInstant = "2026-09-24T22:00:00Z";
    private const string SampleBasePath = "/api/v1/documents/" + SampleId;

    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        // Only a concrete schema can carry an example; a reference node cannot.
        if (schema is not OpenApiSchema concrete)
        {
            return;
        }

        var example = BuildExample(context.Type);
        if (example is not null)
        {
            concrete.Example = example;
        }
    }

    private static JsonNode? BuildExample(Type type)
    {
        if (type == typeof(UploadAcceptedResponse))
        {
            return new JsonObject
            {
                ["id"] = SampleId,
                ["protocol"] = SampleProtocol,
                ["status"] = "QUEUED",
                ["statusUrl"] = SampleBasePath + "/status",
                ["documentUrl"] = SampleBasePath,
                ["contentUrl"] = SampleBasePath + "/content"
            };
        }

        if (type == typeof(DocumentStatusResponse))
        {
            return new JsonObject
            {
                ["id"] = SampleId,
                ["protocol"] = SampleProtocol,
                ["status"] = "QUEUED",
                ["detectedDocumentType"] = null,
                ["classificationConfidence"] = null,
                ["uploadedAt"] = SampleInstant,
                ["completedAt"] = null,
                ["lastError"] = null
            };
        }

        if (type == typeof(DocumentSummaryResponse))
        {
            return BuildSummary();
        }

        if (type == typeof(DocumentDetailResponse))
        {
            return BuildDetail();
        }

        if (type == typeof(PagedResponse<DocumentSummaryResponse>))
        {
            return new JsonObject
            {
                ["items"] = new JsonArray(BuildSummary()),
                ["page"] = 1,
                ["pageSize"] = 20,
                ["totalCount"] = 1,
                ["totalPages"] = 1
            };
        }

        return null;
    }

    private static JsonObject BuildSummary() => new()
    {
        ["id"] = SampleId,
        ["protocol"] = SampleProtocol,
        ["fileName"] = "cartao-cnpj.pdf",
        ["mimeType"] = "application/pdf",
        ["sizeBytes"] = 184_320,
        ["pageCount"] = 1,
        ["channel"] = "API",
        ["status"] = "QUEUED",
        ["expectedDocumentType"] = null,
        ["detectedDocumentType"] = null,
        ["classificationConfidence"] = null,
        ["externalReference"] = "CLIENTE-123",
        ["uploadedAt"] = SampleInstant,
        ["completedAt"] = null,
        ["links"] = BuildLinks()
    };

    private static JsonObject BuildDetail() => new()
    {
        ["id"] = SampleId,
        ["protocol"] = SampleProtocol,
        ["status"] = "QUEUED",
        ["upload"] = new JsonObject
        {
            ["fileName"] = "cartao-cnpj.pdf",
            ["channel"] = "API",
            ["uploadedAt"] = SampleInstant,
            ["completedAt"] = null,
            ["externalReference"] = "CLIENTE-123",
            ["expectedDocumentType"] = null,
            ["mimeType"] = "application/pdf",
            ["sizeBytes"] = 184_320,
            ["pageCount"] = 1,
            ["sha256"] = "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9"
        },
        ["classification"] = null,
        ["extraction"] = null,
        ["lastError"] = null,
        ["timeline"] = new JsonArray(
            new JsonObject
            {
                ["eventType"] = "RECEIVED",
                ["stage"] = "RECEIVED",
                ["details"] = null,
                ["occurredAt"] = SampleInstant
            },
            new JsonObject
            {
                ["eventType"] = "STORED",
                ["stage"] = "STORED",
                ["details"] = null,
                ["occurredAt"] = SampleInstant
            },
            new JsonObject
            {
                ["eventType"] = "QUEUED",
                ["stage"] = "QUEUED",
                ["details"] = null,
                ["occurredAt"] = SampleInstant
            }),
        ["links"] = BuildLinks()
    };

    private static JsonObject BuildLinks() => new()
    {
        ["self"] = SampleBasePath,
        ["status"] = SampleBasePath + "/status",
        ["content"] = SampleBasePath + "/content",
        ["download"] = SampleBasePath + "/content?download=true"
    };
}
