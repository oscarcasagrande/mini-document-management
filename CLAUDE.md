# CLAUDE.md — DocReader PoC

Contexto para agentes que trabalham neste repositório. A fonte de verdade de produto e
arquitetura é `docs/PRD.md`; este arquivo resume o que precisa estar na cabeça antes de editar código.

## O que é

PoC local de ingestão, OCR, classificação e extração de documentos brasileiros, executada
inteiramente por `docker compose up`. Sem cloud, sem API proprietária, sem autenticação.

Plano de execução em etapas (PRD §26).

**Etapa 1 — concluída e aprovada.** Compose completo, upload pela API, persistência, lista,
visualização/download e Swagger.

**Etapa 2 — OCR ponta a ponta: implementada, com aceite via `docker compose` executado.** Aguarda
aprovação. Decisão de engine e números em `docs/adr/0002-*.md`; evidência em `docs/bench/`.

- `ocr-service` real (FastAPI + PaddleOCR PP-OCRv5 mobile, `paddlepaddle` 3.2.2 com oneDNN),
  `POST /v1/ocr/page` uma página por chamada, concorrência 1, limite de 3 GiB;
- `IDocumentOcrProvider` (`PaddleOcrServiceProvider`) e laço de consumo no worker;
- fila em `PostgresProcessingQueue` (`FOR UPDATE SKIP LOCKED`, ADR 0001) com heartbeat por página
  (`locked_at`), fencing por tentativa, retry com backoff (3 tentativas) e um job vivo por documento;
- classificador por regras (`rules-1.0.0`) e extrator `BR_CPF_CARD` (cpf, name, birthDate) com
  `validationStatus` e códigos (`CHECK_DIGIT_VALID`, `CHECK_DIGIT_INVALID`, `DATE_VALID`,
  `NO_LABEL_NEARBY`);
- `extractions` e `extracted_fields` (migration `AddOcrResults`), endpoints `/text`, `/result` e
  `/reprocess`, UI com campos, texto bruto e progresso.

**Pendência obrigatória antes da Etapa 3:** página A4 leva 23–47 s no OCR e o requisito é 5 páginas
em 90 s (~18 s/página). As hipóteses a medir estão no ADR 0002. Também fora da Etapa 2: PDF com
camada de texto nativa (RF-009), orientação/deskew, PP-StructureV3 e `/document-types`.

## Estrutura do repositório

```text
/apps
  /api              DocReader.Api — ASP.NET Core (net10.0), controllers + Swagger
  /worker           DocReader.Worker — Worker Service; consome a fila e chama o ocr-service
  /web-bff          Next.js (App Router) + TypeScript; UI e BFF
/services
  /ocr-service      FastAPI + PaddleOCR; POST /v1/ocr/page, /health (readiness) e /health/live
/src
  /DocReader.Domain          entidades, enums, protocolo, regras puras; sem dependências
  /DocReader.Application     casos de uso e contratos (IFileStorage, IProcessingQueue, ...)
  /DocReader.Infrastructure  EF Core/Npgsql, migrations, storage local, fila no Postgres
  /DocReader.Api.Contracts   DTOs públicos (request/response) da API v1
/tests
  /unit /integration /e2e /accuracy
/schemas/documents   JSON Schemas por tipo documental (Etapa 3)
/samples/synthetic   amostras sintéticas para teste manual
/deploy/docker       Dockerfiles (contexto de build = raiz do repo)
/docs
  PRD.md             PRD + SDD
  /adr               decisões de arquitetura
  /api               artefatos de contrato exportados
/scripts             helpers de desenvolvimento
DocReader.slnx             solução (formato slnx, exige SDK 10.x)
Directory.Build.props      TFM, nullable, warnings-as-errors
Directory.Packages.props   versões centralizadas de pacote
global.json                SDK 10.x e runner Microsoft.Testing.Platform
docker-compose.yml         compose de aceite (postgres e ocr sem porta publicada)
docker-compose.dev.yml     override de desenvolvimento (publica postgres e ocr)
.env.example               sem segredos reais
```

Dependências entre projetos: `Domain` ← `Application` ← `Infrastructure` ← `Api`.
`Api.Contracts` referencia apenas `Domain`, e só pelos enums, para que um status tenha a mesma
grafia no DTO, no banco e no log. Nunca faça `Domain` depender de EF Core, nem `Application` de
Npgsql.

## Comandos

### Subir e aceitar

```bash
docker compose up --build            # sobe web-bff, api, worker, ocr-service, postgres
docker compose ps                    # estado e health de cada serviço
docker compose logs -f api           # logs estruturados em stdout
docker compose restart api worker    # teste de persistência entre reinícios
docker compose down                  # para tudo, preserva volumes
docker compose down -v               # apaga postgres-data e document-storage
```

Com portas de desenvolvimento para postgres e ocr-service:

```bash
docker compose -f docker-compose.yml -f docker-compose.dev.yml up --build
```

Endereços padrão: UI `http://localhost:3000`, API `http://localhost:8080`,
Swagger `http://localhost:8080/swagger`, OpenAPI `http://localhost:8080/swagger/v1/swagger.json`.

### .NET

O alvo é `net10.0` (LTS vigente). Se o SDK local não for 10.x, use o helper, que roda o SDK
em contêiner com o repositório montado:

```bash
dotnet build DocReader.slnx                       # SDK local 10.x
bash scripts/dotnet.sh build DocReader.slnx       # via contêiner
pwsh scripts/dotnet.ps1 build DocReader.slnx      # equivalente no Windows
bash scripts/dotnet.sh test tests/unit/DocReader.UnitTests
```

Os testes usam xunit.v3 sobre Microsoft.Testing.Platform. O `global.json` faz o opt-in
(`"test": { "runner": "Microsoft.Testing.Platform" }`); sem ele o `dotnet test` do SDK 10 falha
tentando usar o VSTest. `bash scripts/dotnet.sh run --project tests/unit/DocReader.UnitTests`
executa a suíte direto, sem passar pelo `dotnet test`.

### Migrations

Migrations são versionadas em `src/DocReader.Infrastructure/Migrations` e **nunca** aplicadas
por `EnsureCreated`. No compose, `api` aplica migrations no start porque
`DocReader__Database__RunMigrationsOnStartup=true`; o padrão em `appsettings.json` é `false`.
Com migrations pendentes e a flag desligada, `/health/ready` reprova.

```bash
bash scripts/dotnet.sh ef migrations add <Nome> \
  --project src/DocReader.Infrastructure --startup-project src/DocReader.Infrastructure
bash scripts/dotnet.sh ef database update \
  --project src/DocReader.Infrastructure --startup-project src/DocReader.Infrastructure
```

### web-bff

```bash
cd apps/web-bff
npm ci && npm run dev      # usa DOCREADER_API_BASE_URL (padrão http://api:8080)
npm run build
npm run typecheck          # o gate de qualidade do front nesta etapa é o tsc, não há ESLint
```

`src/lib/api.ts` é `server-only`: importa `next/headers` e conhece o endereço da API. Nada em
`components/` pode importá-lo. O que o browser precisa de caminho vive em `src/lib/routes.ts`.

## Convenções obrigatórias

Vêm do PRD (§16, §20, §21) e valem para todo código novo.

### API e contratos

- JSON **camelCase** em request e response, inclusive em chaves de dicionário.
- Datas e horas sempre **UTC**, ISO 8601 com `Z`. Nada de `DateTime.Now`: use
  `TimeProvider`/`DateTimeOffset.UtcNow` e colunas `timestamptz`.
- Todo erro responde **`application/problem+json`** (RFC 9457), com `type`, `title`, `status`,
  `detail`, `instance` e a extensão `correlationId`. Não escreva erro em formato próprio,
  não devolva HTML de exceção, não vaze stack trace.
- Códigos combinados: `202` upload aceito (com `Location`), `404` inexistente,
  `409` conflito (inclui `Idempotency-Key` reutilizada com outro arquivo), `413` acima do
  limite de tamanho, `415` formato/assinatura não suportada, `422` conteúdo inválido para
  processamento (ex.: excede o limite de páginas).
- **`X-Correlation-Id` em todas as respostas.** Aceite o header do cliente quando presente e
  válido; caso contrário gere um. Propague para worker e ocr-service.
- Versionamento no path: `/api/v1/...`. Mudança quebrada exige `/api/v2`.
- Todo endpoint público precisa aparecer no OpenAPI com schemas e exemplos de sucesso e erro
  (DoD do §17 do PRD).

### Log e privacidade

- **Nenhum conteúdo documental em log.** Nunca logue texto extraído, valor de campo, CPF,
  CNPJ, bytes do arquivo ou o nome original completo do arquivo. Logue `documentId`,
  `protocol`, `correlationId`, `mimeType`, `sizeBytes`, `pageCount`, estágio, duração e código
  de erro. Se precisar identificar o arquivo, use o SHA-256 ou a `storageKey`.
- Logs estruturados em stdout, um evento por linha, sem cor e sem arte ASCII.
- Mensagem de erro devolvida ao cliente é orientação, não despejo de exceção.

### Armazenamento

- Chaves de storage derivam de UUID, nunca do nome enviado pelo usuário. Extensão vem do MIME
  **detectado** por assinatura, restrito à allowlist. O nome original é apenas metadado.
- Nada de path traversal: valide a chave antes de abrir e resolva sempre sob a raiz configurada.
- O binário vive no volume `document-storage`; o banco guarda metadado e caminho lógico.
- Acesso a arquivo passa por `IFileStorage`. OCR passa por `IDocumentOcrProvider`. Fila passa
  por `IProcessingQueue`. Não chame `File.*` nem SQL de fila fora de `Infrastructure`.

### Segurança mínima

- Valide MIME real por assinatura (magic bytes) e rejeite divergência do `Content-Type`
  declarado; bloqueie executável disfarçado.
- Respeite `Upload:MaxSizeBytes` e `Upload:MaxPageCount`, ambos configuráveis.
- SQL sempre parametrizado (EF Core ou `NpgsqlParameter`); nunca interpolação de string.
- `postgres` e `ocr-service` não publicam porta no compose de aceite.
- CORS restrito a `DocReader:Security:AllowedCorsOrigins`.
- A UI exibe aviso de ambiente anônimo; `ALLOW_ANONYMOUS_ACCESS=false` bloqueia `/api/v1`
  com `503`, porque não existe provedor de autenticação na PoC.

### Estilo

- C#: nullable e warnings-as-errors ligados, `async`/`await` com `CancellationToken` em toda
  chamada de I/O, um tipo por arquivo, sem `#region`, sem comentário que repita o código.
- TypeScript: `strict`, sem `any` implícito, Server Components por padrão e `"use client"`
  só onde há estado ou evento.
- Python: FastAPI com type hints e Pydantic.
- Mensagens de commit e código em inglês; documentação de produto em português.
