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

        if (type == typeof(ExtractionDiagnosticsResponse))
        {
            return BuildExtractionDiagnostics();
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

        if (type == typeof(WebhookSubscriptionCreatedResponse))
        {
            var created = BuildWebhookSubscription();
            created["secret"] = "9f2c1ab5d0e74c3c8a6b21f04d5e9a77c3b1e8d24f6a90b5c7d1e3f8a2b4c6d0";

            return created;
        }

        if (type == typeof(WebhookSubscriptionResponse))
        {
            return BuildWebhookSubscription();
        }

        if (type == typeof(PagedResponse<WebhookSubscriptionResponse>))
        {
            return new JsonObject
            {
                ["items"] = new JsonArray(BuildWebhookSubscription()),
                ["page"] = 1,
                ["pageSize"] = 20,
                ["totalCount"] = 1,
                ["totalPages"] = 1
            };
        }

        if (type == typeof(WebhookDeliveryResponse))
        {
            return BuildWebhookDelivery();
        }

        if (type == typeof(PagedResponse<WebhookDeliveryResponse>))
        {
            return new JsonObject
            {
                ["items"] = new JsonArray(BuildWebhookDelivery()),
                ["page"] = 1,
                ["pageSize"] = 20,
                ["totalCount"] = 1,
                ["totalPages"] = 1
            };
        }

        if (type == typeof(StorageRepositoryResponse))
        {
            return BuildStorageRepository();
        }

        if (type == typeof(PagedResponse<StorageRepositoryResponse>))
        {
            return new JsonObject
            {
                ["items"] = new JsonArray(BuildStorageRepository()),
                ["page"] = 1,
                ["pageSize"] = 20,
                ["totalCount"] = 1,
                ["totalPages"] = 1
            };
        }

        if (type == typeof(RetentionPolicyResponse))
        {
            return BuildRetentionPolicy();
        }

        if (type == typeof(PagedResponse<RetentionPolicyResponse>))
        {
            return new JsonObject
            {
                ["items"] = new JsonArray(BuildRetentionPolicy()),
                ["page"] = 1,
                ["pageSize"] = 20,
                ["totalCount"] = 1,
                ["totalPages"] = 1
            };
        }

        if (type == typeof(ProductServiceResponse))
        {
            return BuildProductService();
        }

        if (type == typeof(PagedResponse<ProductServiceResponse>))
        {
            return new JsonObject
            {
                ["items"] = new JsonArray(BuildProductService()),
                ["page"] = 1,
                ["pageSize"] = 20,
                ["totalCount"] = 1,
                ["totalPages"] = 1
            };
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

    private static JsonObject BuildWebhookSubscription() => new()
    {
        ["id"] = "0199c1f0-4444-7a10-9c44-2f1d8e6b4a21",
        ["url"] = "https://webhook.site/6c0e2f6a-1b7d-4c8e-9a5f-3d2b1c0e9f8a",
        ["events"] = new JsonArray("document.completed", "document.failed"),
        ["productService"] = BuildProductReference(),
        ["active"] = true,
        ["createdAt"] = SampleInstant,
        ["updatedAt"] = SampleInstant
    };

    private static JsonObject BuildWebhookDelivery() => new()
    {
        ["id"] = "0199c1f0-5555-7a10-9c44-2f1d8e6b4a21",
        ["documentId"] = SampleId,
        ["event"] = "document.completed",
        ["status"] = "SUCCEEDED",
        ["attemptCount"] = 1,
        ["nextAttemptAt"] = SampleInstant,
        ["lastAttemptAt"] = SampleInstant,
        ["lastStatusCode"] = 200,
        ["lastError"] = null,
        ["createdAt"] = SampleInstant,
        ["completedAt"] = SampleInstant
    };

    private static JsonObject BuildStorageRepository() => new()
    {
        ["id"] = "0199c1f0-3333-7a10-9c44-2f1d8e6b4a21",
        ["code"] = "ARQUIVO-DB",
        ["name"] = "Arquivo no banco de dados",
        ["provider"] = "DATABASE",
        ["isDefault"] = false,
        ["active"] = true,
        ["hasConnectionConfig"] = false,
        ["isImplemented"] = true,
        ["createdAt"] = SampleInstant,
        ["updatedAt"] = SampleInstant
    };

    private static JsonObject BuildRetentionPolicy() => new()
    {
        ["id"] = "0199c1f0-2222-7a10-9c44-2f1d8e6b4a21",
        ["documentType"] = "BR_CNPJ_CARD",
        ["productService"] = BuildProductReference(),
        ["retentionDays"] = 365,
        ["scope"] = "DOCUMENT_TYPE_AND_PRODUCT_SERVICE",
        ["isGlobal"] = false,
        ["createdAt"] = SampleInstant,
        ["updatedAt"] = SampleInstant
    };

    private static JsonObject BuildProductReference() => new()
    {
        ["id"] = "0199c1f0-1111-7a10-9c44-2f1d8e6b4a21",
        ["code"] = "CONTA-PJ",
        ["name"] = "Abertura de conta PJ"
    };

    private static JsonObject BuildProductService() => new()
    {
        ["id"] = "0199c1f0-1111-7a10-9c44-2f1d8e6b4a21",
        ["code"] = "CONTA-PJ",
        ["name"] = "Abertura de conta PJ",
        ["active"] = true,
        ["storageRepository"] = new JsonObject
        {
            ["id"] = "0199c1f0-3333-7a10-9c44-2f1d8e6b4a21",
            ["code"] = "ARQUIVO-DB",
            ["name"] = "Arquivo no banco de dados"
        },
        ["createdAt"] = SampleInstant,
        ["updatedAt"] = SampleInstant
    };

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
        ["productService"] = BuildProductReference(),
        ["expiresAt"] = "2027-09-24T22:00:00Z",
        ["uploadedAt"] = SampleInstant,
        ["completedAt"] = null,
        ["links"] = BuildLinks()
    };

    private static JsonObject BuildDetail() => new()
    {
        ["id"] = SampleId,
        ["protocol"] = SampleProtocol,
        ["status"] = "QUEUED",
        ["productService"] = BuildProductReference(),
        ["retention"] = new JsonObject
        {
            ["expiresAt"] = "2027-09-24T22:00:00Z",
            ["retentionDays"] = 365,
            ["policy"] = new JsonObject
            {
                ["id"] = "0199c1f0-2222-7a10-9c44-2f1d8e6b4a21",
                ["scope"] = "DOCUMENT_TYPE_AND_PRODUCT_SERVICE",
                ["documentType"] = "BR_CNPJ_CARD",
                ["productServiceCode"] = "CONTA-PJ",
                ["retentionDays"] = 365
            },
            ["purgedAt"] = null
        },
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

    private static JsonObject BuildExtractionDiagnostics() => new()
    {
        ["id"] = SampleId,
        ["protocol"] = SampleProtocol,
        ["status"] = "COMPLETED",
        ["extractedAt"] = SampleInstant,
        ["recordedExtractorVersion"] = "br-cnh-1.0.0",
        ["documentType"] = "BR_CNH",
        ["documentTypeSource"] = "RECORDED",
        ["extractorVersion"] = "br-cnh-1.1.0",
        ["ocrInput"] = "BLOCKS",
        ["note"] = null,
        ["coverage"] = new JsonObject
        {
            ["expected"] = 8,
            ["extracted"] = 7,
            ["valid"] = 7,
            ["extractedRatio"] = 0.875,
            ["validRatio"] = 0.875
        },
        ["fields"] = new JsonArray(
            new JsonObject
            {
                ["path"] = "name",
                ["status"] = "VALID",
                ["reason"] = "FOUND",
                ["explanation"] = "The value was read.",
                ["messages"] = new JsonArray(),
                ["raw"] = "MARIA APARECIDA DA SILVA SOUZA",
                ["normalized"] = "MARIA APARECIDA DA SILVA SOUZA",
                ["confidence"] = 0.99,
                ["page"] = 1,
                ["boundingBox"] = new JsonArray(159, 174, 486, 174, 486, 198, 159, 198),
                ["recordedStatus"] = "NOT_FOUND",
                ["rule"] = new JsonObject
                {
                    ["labels"] = new JsonArray(
                        new JsonObject { ["label"] = "NOME", ["foundOnLines"] = new JsonArray() },
                        new JsonObject { ["label"] = "NOME E SOBRENOME", ["foundOnLines"] = new JsonArray(6) }),
                    ["candidatesSeen"] = 1,
                    ["candidates"] = new JsonArray(new JsonObject
                    {
                        ["lineIndex"] = 8,
                        ["page"] = 1,
                        ["text"] = "MARIA APARECIDA DA SILVA SOUZA",
                        ["penalty"] = 0,
                        ["accepted"] = true
                    })
                }
            },
            new JsonObject
            {
                ["path"] = "category",
                ["status"] = "NOT_FOUND",
                ["reason"] = "VALUE_EMPTY",
                ["explanation"] = "The label was found on line 22, but no value was next to it, to its right or below it.",
                ["messages"] = new JsonArray(),
                ["raw"] = null,
                ["normalized"] = null,
                ["confidence"] = null,
                ["page"] = null,
                ["boundingBox"] = new JsonArray(),
                ["recordedStatus"] = "NOT_FOUND",
                ["rule"] = new JsonObject
                {
                    ["labels"] = new JsonArray(new JsonObject { ["label"] = "CAT HAB", ["foundOnLines"] = new JsonArray(22) }),
                    ["candidatesSeen"] = 0,
                    ["candidates"] = new JsonArray()
                }
            }),
        ["pages"] = new JsonArray(new JsonObject
        {
            ["page"] = 1,
            ["blocks"] = new JsonArray(new JsonObject
            {
                ["index"] = 6,
                ["text"] = "2e 1 NOME E SOBRENOME",
                ["confidence"] = 0.95,
                ["boundingBox"] = new JsonArray(154, 154, 361, 154, 361, 172, 154, 172)
            })
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
