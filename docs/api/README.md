Artefatos de contrato exportados da API.

O OpenAPI é gerado pelo código e servido em `/swagger/v1/swagger.json`; `openapi-v1.json` é a cópia
exportada. Para reexportar:

    curl -s http://localhost:8080/swagger/v1/swagger.json -o docs/api/openapi-v1.json

Endpoints da v1:

| Método | Caminho | Etapa |
|---|---|---|
| `POST` | `/api/v1/documents` | 1 |
| `GET` | `/api/v1/documents` | 1 |
| `GET` / `DELETE` | `/api/v1/documents/{id}` | 1 |
| `GET` | `/api/v1/documents/by-protocol/{protocol}` | 1 |
| `GET` | `/api/v1/documents/{id}/status` | 1 |
| `GET` | `/api/v1/documents/{id}/content` | 1 |
| `GET` | `/api/v1/documents/{id}/text` | 2 |
| `GET` | `/api/v1/documents/{id}/result` | 2 |
| `POST` | `/api/v1/documents/{id}/reprocess` | 2 |
| `GET` | `/api/v1/documents/{id}/classification-diagnostics` | 4 |

`/text`, `/result` e `/classification-diagnostics` respondem `409` (`RESULT_NOT_READY`) enquanto não há extração; `/reprocess`
responde `202`, `404` se o documento não existe e `409` (`REPROCESS_CONFLICT`) se o documento está na
fila ou em processamento, ou foi rejeitado. Todo erro é `application/problem+json`.
