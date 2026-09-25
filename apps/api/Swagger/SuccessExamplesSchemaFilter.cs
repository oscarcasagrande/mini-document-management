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
    private const string SampleModelVersion =
        "PP-OCRv5 PP-OCRv5_mobile_det + latin_PP-OCRv5_mobile_rec (paddleocr 3.7.0, paddlepaddle 3.3.1, mkldnn=off)";
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
                ["lastError"] = null,
                ["processing"] = BuildProcessing()
            };
        }

        if (type == typeof(DocumentTextResponse))
        {
            return BuildText();
        }

        if (type == typeof(ClassificationDiagnosticsResponse))
        {
            return BuildClassificationDiagnostics();
        }

        if (type == typeof(DocumentResultResponse))
        {
            return BuildResult();
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
        ["processing"] = BuildProcessing(),
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

    private static JsonObject BuildProcessing() => new()
    {
        ["jobStatus"] = "RUNNING",
        ["attempt"] = 1,
        ["maxAttempts"] = 3,
        ["pagesCompleted"] = 0,
        ["pageCount"] = 1,
        ["nextAttemptAt"] = null
    };

    private static JsonObject BuildText() => new()
    {
        ["id"] = SampleId,
        ["protocol"] = SampleProtocol,
        ["status"] = "COMPLETED",
        ["ocrProvider"] = "paddleocr",
        ["ocrModelVersion"] = SampleModelVersion,
        ["extractedAt"] = SampleInstant,
        ["pages"] = new JsonArray(new JsonObject
        {
            ["page"] = 1,
            ["text"] = "REPUBLICA FEDERATIVA DO BRASIL\nCADASTRO DE PESSOAS FISICAS\nNUMERO DE INSCRICAO\n111.444.777-35\nNOME\nMARIA APARECIDA DA SILVA SOUZA\nNASCIMENTO\n14/03/1985"
        })
    };

    private static JsonObject BuildClassificationDiagnostics() => new()
    {
        ["id"] = SampleId,
        ["protocol"] = SampleProtocol,
        ["status"] = "COMPLETED",
        ["extractedAt"] = SampleInstant,
        ["recorded"] = new JsonObject
        {
            ["detectedType"] = "UNKNOWN",
            ["confidence"] = null,
            ["classifierVersion"] = "rules-1.0.0"
        },
        ["current"] = new JsonObject
        {
            ["classifierVersion"] = "rules-2.0.0",
            ["documentType"] = "BR_CIN",
            ["confidence"] = 0.85,
            ["reason"] = "BR_CIN chosen with score 0.85 (threshold 0.60), best of 7 types tried.",
            ["minimumScoreOverride"] = null,
            ["textLength"] = 612
        },
        ["candidates"] = new JsonArray(
            new JsonObject
            {
                ["documentType"] = "BR_CIN",
                ["score"] = 0.85,
                ["threshold"] = 0.6,
                ["accepted"] = true,
                ["reason"] = "Score 0.85 reached the threshold 0.60. Found: title, republic, registration-number, birth-date, parentage, issue-date.",
                ["evidence"] = new JsonArray(
                    BuildEvidence("title", 0.45, true, "REGISTRO DE IDENTIDADE CIVIL", "EXACT", 0),
                    BuildEvidence("parentage", 0.10, true, "FILIACAO", "FUZZY", 1),
                    BuildEvidence("place-of-birth", 0.05, false, null, null, null)),
                ["counterEvidence"] = new JsonArray(
                    BuildEvidence("cnh-title", 0.60, false, null, null, null))
            }),
        ["pages"] = new JsonArray(new JsonObject
        {
            ["page"] = 1,
            ["text"] = "REPUBLICA FEDERATIVA DO BRASIL\nREGISTRO DE IDENTIDADE CIVIL\nDATA DE NASC / DATE OF BIRTH\nFILUACAO"
        })
    };

    private static JsonObject BuildEvidence(string name, double weight, bool matched, string? pattern, string? kind, int? edits) => new()
    {
        ["name"] = name,
        ["weight"] = weight,
        ["matched"] = matched,
        ["matchedPattern"] = pattern,
        ["matchKind"] = kind,
        ["edits"] = edits
    };

    private static JsonObject BuildResult() => new()
    {
        ["id"] = SampleId,
        ["protocol"] = SampleProtocol,
        ["status"] = "COMPLETED",
        ["upload"] = new JsonObject
        {
            ["fileName"] = "cartao-cpf.png",
            ["channel"] = "API",
            ["uploadedAt"] = SampleInstant,
            ["externalReference"] = "CLIENTE-123"
        },
        ["classification"] = new JsonObject
        {
            ["detectedType"] = "BR_CPF_CARD",
            ["confidence"] = 1.0,
            ["classifierVersion"] = "rules-1.0.0"
        },
        ["extraction"] = new JsonObject
        {
            ["ocrProvider"] = "paddleocr",
            ["ocrModelVersion"] = SampleModelVersion,
            ["extractorVersion"] = "br-cpf-card-1.0.0",
            ["schemaVersion"] = 1,
            ["overallConfidence"] = 0.9957,
            ["extractedAt"] = SampleInstant,
            ["fields"] = new JsonObject
            {
                ["cpf"] = BuildField("111.444.777-35", "11144477735", 1.0, "VALID", "CHECK_DIGIT_VALID"),
                ["name"] = BuildField("MARIA APARECIDA DA SILVA SOUZA", "MARIA APARECIDA DA SILVA SOUZA", 0.9871, "VALID"),
                ["birthDate"] = BuildField("14/03/1985", "1985-03-14", 1.0, "VALID", "DATE_VALID")
            }
        }
    };

    private static JsonObject BuildField(string raw, string normalized, double confidence, string status, params string[] messages)
    {
        var codes = new JsonArray();
        foreach (var message in messages)
        {
            codes.Add(message);
        }

        return new JsonObject
        {
            ["raw"] = raw,
            ["normalized"] = normalized,
            ["confidence"] = confidence,
            ["validationStatus"] = status,
            ["validationMessages"] = codes,
            ["evidence"] = new JsonObject
            {
                ["page"] = 1,
                ["boundingBox"] = new JsonArray(110.0, 332.0, 470.0, 332.0, 470.0, 380.0, 110.0, 380.0)
            }
        };
    }

    private static JsonObject BuildLinks() => new()
    {
        ["self"] = SampleBasePath,
        ["status"] = SampleBasePath + "/status",
        ["content"] = SampleBasePath + "/content",
        ["download"] = SampleBasePath + "/content?download=true",
        ["text"] = SampleBasePath + "/text",
        ["result"] = SampleBasePath + "/result",
        ["reprocess"] = SampleBasePath + "/reprocess"
    };
}
