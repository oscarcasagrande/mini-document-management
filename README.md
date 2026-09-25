# DocReader PoC

Prova de conceito local de leitura de documentos: ingestão por web e API, OCR local, classificação,
extração estruturada e consulta — tudo em contêineres, sem nuvem e sem API proprietária.

PRD e SDD completos em [`docs/PRD.md`](docs/PRD.md). Decisões de arquitetura em [`docs/adr`](docs/adr).

## Etapa atual: 3 — extração estruturada

O plano de execução (PRD §26) tem quatro etapas. A Etapa 1 (walking skeleton) foi aprovada, a Etapa 2 ligou
o OCR ponta a ponta e **a Etapa 3 acrescenta os extratores estruturados**:

| Tipo | `documentType` | Campos principais | Validação determinística |
|---|---|---|---|
| Cartão de CPF | `BR_CPF_CARD` | cpf, nome, nascimento | DV do CPF, data |
| CIN e RG | `BR_CIN` | nome, cpf, rg, datas, naturalidade, filiação, MRZ | DV do CPF, datas, MRZ (ICAO 9303) |
| CNH | `BR_CNH` | nome, cpf, registro, categoria, datas | DV do CPF, datas, categoria |
| Comprovante de residência | `BR_PROOF_OF_ADDRESS` | titular, endereço, CEP, cidade, UF, referência, vencimento | DV de CPF/CNPJ, CEP, UF |
| Cartão CNPJ | `BR_CNPJ_CARD` | cnpj, nome empresarial, atividade, endereço, situação | DV do **CNPJ alfanumérico**, datas |
| CCMEI | `BR_CCMEI` | cnpj, nome, capital, atividade, endereço, empresário | DV do CNPJ e do CPF, valor |
| Contrato social | `BR_SOCIAL_CONTRACT` | denominação, cnpj, capital, sede, objeto, data, sócios | DV do CNPJ e dos CPFs dos sócios |

Qualquer outro tipo é aceito, lido pelo OCR e devolvido como `UNKNOWN`, com o texto bruto e sem campos. Os
campos, as regras e o que **não** é validado estão em [`schemas/documents`](schemas/documents).

**O que não foi medido.** Os extratores foram escritos e testados sobre amostras sintéticas
([`samples/synthetic/documents`](samples/synthetic/documents)) com a estrutura de rótulo e valor dos
documentos reais, não sobre documentos reais. Layouts de outros estados, concessionárias e juntas vão errar
de formas que a suíte atual não vê; a Etapa 4 (avaliação) mede isso.

**Latência.** A medição de seguimento do [ADR 0002](docs/adr) fechou a pendência de páginas A4: o alvo de
≤ 18 s por página é atingido sem limite de CPU no contêiner (pior caso 15,8 s) e não é atingido limitando-o
a 4 CPUs (28 s). `OCR_MODEL_PROFILE` troca o modelo (`ppocrv6-small` e `ppocrv6-tiny` são mais rápidos e
menos exatos); o padrão não mudou.

Para ver o resultado de um documento:

```bash
curl -s "http://localhost:8080/api/v1/documents/$ID/text"     # texto bruto por página
curl -s "http://localhost:8080/api/v1/documents/$ID/result"   # tipo, campos, confiança e validação
curl -s -X POST "http://localhost:8080/api/v1/documents/$ID/reprocess"   # 202; 409 se já há job em andamento
```

Um campo reprovado nas regras sai como `validationStatus: "INVALID"` com o valor lido preservado; um que o
documento não traz, como `NOT_FOUND`; nada é corrigido, descartado nem inventado.

### Roteiro de aceite da Etapa 3

```bash
docker compose up -d --build
# Swagger em http://localhost:8080/swagger: POST /api/v1/documents com cada arquivo de
# samples/synthetic/documents, depois a tela de detalhe em http://localhost:3000/documents/{id}

# ou, automatizado, os seis tipos de uma vez (confere campo a campo contra o expected.json de cada amostra)
docker run --rm -v "$PWD:/w" -w /w python:3.12-slim python tests/e2e/stage3_acceptance.py \
  --api http://host.docker.internal:8080 --web http://host.docker.internal:3000
```

## Subir

Pré-requisitos: Docker Engine e Docker Compose. Nada mais — não é preciso SDK do .NET nem Node na
máquina.

```bash
cp .env.example .env          # opcional; os defaults do compose já funcionam
docker compose up --build
```

| Serviço | Endereço | Observação |
|---|---|---|
| Interface (web-bff) | http://localhost:3000 | upload, lista e detalhe |
| API | http://localhost:8080 | REST v1 |
| Swagger UI | http://localhost:8080/swagger | público, sem login |
| OpenAPI JSON | http://localhost:8080/swagger/v1/swagger.json | |
| Liveness / readiness | http://localhost:8080/health/live · `/health/ready` | |
| PostgreSQL | sem porta publicada | somente rede `internal` |
| ocr-service | sem porta publicada | somente rede `internal` |

Para expor banco e OCR em desenvolvimento:

```bash
docker compose -f docker-compose.yml -f docker-compose.dev.yml up --build
```

> **Sem autenticação.** Qualquer pessoa com acesso à URL pode enviar, listar, visualizar, baixar e
> excluir documentos. Não publique na internet e use apenas documentos sintéticos, mascarados ou
> autorizados. `ALLOW_ANONYMOUS_ACCESS=false` fecha `/api/v1` com `503`, porque a PoC não tem
> provedor de autenticação para onde cair.

## Amostras sintéticas

Os arquivos de teste são gerados, não versionados como binário:

```bash
node scripts/make-samples.mjs
```

| Arquivo | Para quê |
|---|---|
| `comprovante-1-pagina.pdf` | caminho felizardo |
| `contrato-3-paginas.pdf` | contagem de páginas |
| `dossie-60-paginas.pdf` | limite de páginas → `422` |
| `documento-digitalizado.png` | imagem de página única |
| `documento-frente-verso.tif` | TIFF multipágina |
| `executavel-disfarcado.pdf` | executável com nome de PDF → `415` |
| `pdf-corrompido.pdf` | assinatura válida, corpo inválido → `422` |

## Exemplos com curl

Upload:

```bash
curl -i -X POST http://localhost:8080/api/v1/documents \
  -F "file=@samples/synthetic/comprovante-1-pagina.pdf;type=application/pdf" \
  -F "externalReference=CLIENTE-123"
```

```http
HTTP/1.1 202 Accepted
Location: /api/v1/documents/0199c1f0-7b3a-7a10-9c44-2f1d8e6b4a21
X-Correlation-Id: 9f1c1d0f2a5b4c8e9d7a6b5c4d3e2f10
```

```json
{
  "id": "0199c1f0-7b3a-7a10-9c44-2f1d8e6b4a21",
  "protocol": "DOC-20260924-000001",
  "status": "QUEUED",
  "statusUrl": "/api/v1/documents/0199c1f0-7b3a-7a10-9c44-2f1d8e6b4a21/status",
  "documentUrl": "/api/v1/documents/0199c1f0-7b3a-7a10-9c44-2f1d8e6b4a21",
  "contentUrl": "/api/v1/documents/0199c1f0-7b3a-7a10-9c44-2f1d8e6b4a21/content"
}
```

Consultas:

```bash
ID=0199c1f0-7b3a-7a10-9c44-2f1d8e6b4a21

curl -s "http://localhost:8080/api/v1/documents?pageSize=5"
curl -s "http://localhost:8080/api/v1/documents?status=QUEUED&channel=API&fileName=comprovante"
curl -s "http://localhost:8080/api/v1/documents/$ID"
curl -s "http://localhost:8080/api/v1/documents/by-protocol/DOC-20260924-000001"
curl -s "http://localhost:8080/api/v1/documents/$ID/status"
```

Visualizar e baixar o original:

```bash
# inline, como o navegador abre
curl -i "http://localhost:8080/api/v1/documents/$ID/content" | head -12

# download como anexo
curl -OJ "http://localhost:8080/api/v1/documents/$ID/content?download=true"
```

Idempotência:

```bash
KEY=$(uuidgen)

# primeira vez: cria
curl -i -X POST http://localhost:8080/api/v1/documents \
  -H "Idempotency-Key: $KEY" \
  -F "file=@samples/synthetic/comprovante-1-pagina.pdf"

# mesma chave, mesmo arquivo: repete a resposta, com Idempotency-Replayed: true
curl -i -X POST http://localhost:8080/api/v1/documents \
  -H "Idempotency-Key: $KEY" \
  -F "file=@samples/synthetic/comprovante-1-pagina.pdf"

# mesma chave, arquivo diferente: 409 em problem+json
curl -i -X POST http://localhost:8080/api/v1/documents \
  -H "Idempotency-Key: $KEY" \
  -F "file=@samples/synthetic/contrato-3-paginas.pdf"
```

Recusas (todas em `application/problem+json`):

```bash
# 415: executável com nome de PDF
curl -i -X POST http://localhost:8080/api/v1/documents \
  -F "file=@samples/synthetic/executavel-disfarcado.pdf;type=application/pdf"

# 422: acima do limite de páginas
curl -i -X POST http://localhost:8080/api/v1/documents \
  -F "file=@samples/synthetic/dossie-60-paginas.pdf"

# 422: assinatura de PDF, estrutura inválida
curl -i -X POST http://localhost:8080/api/v1/documents \
  -F "file=@samples/synthetic/pdf-corrompido.pdf"

# 413: acima do limite de tamanho
head -c 30000000 /dev/urandom > /tmp/grande.bin
cat samples/synthetic/comprovante-1-pagina.pdf /tmp/grande.bin > /tmp/grande.pdf
curl -i -X POST http://localhost:8080/api/v1/documents -F "file=@/tmp/grande.pdf"
```

Excluir (irreversível):

```bash
curl -i -X DELETE "http://localhost:8080/api/v1/documents/$ID"
```

## Roteiro de aceite da Etapa 1

```bash
# 1. subir tudo
docker compose up --build
docker compose ps                       # cinco serviços, api/worker/web-bff/postgres saudáveis

# 2. upload por curl e por Swagger (http://localhost:8080/swagger)
# 3. lista, visualização e download pela interface (http://localhost:3000/documents)

# 4. persistência entre reinícios
docker compose restart
curl -s "http://localhost:8080/api/v1/documents" | head -40   # documentos continuam lá
curl -s -o /dev/null -w "%{http_code}\n" \
  "http://localhost:8080/api/v1/documents/$ID/content"        # 200: o arquivo também

# 5. banco e OCR não expostos
docker compose port postgres 5432       # sem mapeamento
docker compose port ocr-service 8000    # sem mapeamento
```

## Desenvolvimento

O alvo é `net10.0`. Se a máquina não tiver o SDK 10.x, os helpers rodam o SDK em contêiner:

```bash
bash scripts/dotnet.sh build DocReader.slnx
bash scripts/dotnet.sh test tests/unit/DocReader.UnitTests
pwsh scripts/dotnet.ps1 build DocReader.slnx     # equivalente no Windows
```

Interface:

```bash
cd apps/web-bff
npm ci
npm run dev            # http://localhost:3000, usa DOCREADER_API_BASE_URL
npm run typecheck
```

Migrations ficam em `src/DocReader.Infrastructure/Migrations` e nunca são criadas por
`EnsureCreated`. No compose a API aplica as pendentes no start porque
`DocReader__Database__RunMigrationsOnStartup=true`; o padrão em `appsettings.json` é `false`, e com
migration pendente o `/health/ready` reprova.

```bash
bash scripts/dotnet.sh ef migrations add <Nome> \
  --project src/DocReader.Infrastructure --startup-project src/DocReader.Infrastructure
```

## Estrutura

```text
apps/api            API ASP.NET Core + Swagger
apps/worker         Worker Service: consome a fila e chama o ocr-service
apps/web-bff        Next.js: interface e BFF
services/ocr-service  FastAPI + PaddleOCR
src/                Domain, Application, Infrastructure, Api.Contracts
tests/              unit, integration, e2e, accuracy
deploy/docker       Dockerfiles (contexto de build = raiz)
docs/               PRD, ADRs
scripts/            helpers de desenvolvimento
samples/synthetic   amostras geradas
```

Convenções de código, comandos e regras de log estão em [`CLAUDE.md`](CLAUDE.md).

## Licença e dados

PoC interna. Use somente documentos sintéticos, mascarados ou autorizados — nunca documentos
pessoais reais em ambiente compartilhado.
