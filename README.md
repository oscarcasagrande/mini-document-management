# DocReader PoC

Prova de conceito local de leitura de documentos: ingestão por web e API, OCR local, classificação,
extração estruturada e consulta — tudo em contêineres, sem nuvem e sem API proprietária.

PRD e SDD completos em [`docs/PRD.md`](docs/PRD.md). Decisões de arquitetura em [`docs/adr`](docs/adr).
Convenções de código, comandos e armadilhas conhecidas em [`CLAUDE.md`](CLAUDE.md).

## O que o sistema faz

Um documento enviado pela interface ou pela API recebe um protocolo, entra numa fila no PostgreSQL e é lido pelo
worker: OCR local (PaddleOCR, em CPU), classificação por regras e extração de campos com validação
determinística. O original fica sempre consultável, mesmo que o OCR falhe.

| Tipo | `documentType` | Campos principais | Validação determinística |
|---|---|---|---|
| Cartão de CPF | `BR_CPF_CARD` | cpf, nome, nascimento | DV do CPF, data |
| CIN e RG | `BR_CIN` | nome, cpf, rg, datas, naturalidade, filiação, MRZ | DV do CPF, datas, validade vencida, MRZ (ICAO 9303) |
| CNH | `BR_CNH` | nome, cpf, registro, categoria, datas | DV do CPF, **DV do registro**, datas, validade vencida |
| Comprovante de residência | `BR_PROOF_OF_ADDRESS` | titular, endereço, CEP, cidade, UF, referência, vencimento | DV de CPF/CNPJ, CEP, UF |
| Cartão CNPJ | `BR_CNPJ_CARD` | cnpj, nome empresarial, atividade, endereço, situação | DV do **CNPJ alfanumérico**, datas |
| CCMEI | `BR_CCMEI` | cnpj, nome, capital, atividade, endereço, empresário | DV do CNPJ e do CPF, valor |
| Contrato social | `BR_SOCIAL_CONTRACT` | denominação, cnpj, capital, sede, objeto, data, sócios | DV do CNPJ e dos CPFs dos sócios |

Qualquer outro tipo é aceito, lido pelo OCR e devolvido como `UNKNOWN`, com o texto bruto e sem campos. Os
campos, as regras e o que **não** é validado estão em [`schemas/documents`](schemas/documents).

Um campo reprovado nas regras sai como `validationStatus: "INVALID"` com o valor lido preservado; um que o
documento não traz, como `NOT_FOUND`; uma CNH ou CIN com validade vencida continua `VALID`, com a mensagem
`DOCUMENT_EXPIRED`. Nada é corrigido, descartado nem inventado.

**Estado e limites.** As Etapas 1 a 3 do plano (PRD §26) estão implementadas. A Etapa 4 tem o framework de
avaliação pronto ([Avaliação](#avaliação)), mas **a medição em documentos reais não foi feita**: este repositório
não tem dataset real, e os extratores foram escritos e testados sobre amostras **sintéticas**. A decisão de
continuidade e produção depende dessa medição, e a comparação opcional com Tesseract/Docling não foi feita.

## Pré-requisitos

Docker Engine e Docker Compose. Nada mais: não é preciso SDK do .NET, Node nem Python na máquina, porque build,
testes e ferramentas rodam em contêiner. Reserve ~8 GB de memória para a VM do Docker e ~15 GB de disco (a imagem
do OCR tem ~2,5 GB). O SDK do .NET 10 local é opcional, e os comandos abaixo usam o helper em contêiner.

## Rodar o sistema

```bash
cp .env.example .env          # opcional; os defaults do compose já funcionam
docker compose up -d --build
docker compose ps             # os cinco serviços devem estar healthy
```

A primeira subida demora: baixa as imagens base, compila e o `ocr-service` baixa e aquece os modelos (ficam na
imagem). Depois disso `docker compose up -d` leva segundos.

| Serviço | Endereço | Observação |
|---|---|---|
| Interface (web-bff) | http://localhost:3000 | upload, lista e detalhe |
| API | http://localhost:8080 | REST v1 |
| Swagger UI | http://localhost:8080/swagger | público, sem login |
| OpenAPI JSON | http://localhost:8080/swagger/v1/swagger.json | cópia em `docs/api/openapi-v1.json` |
| Liveness / readiness | http://localhost:8080/health/live · `/health/ready` | |
| PostgreSQL | sem porta publicada | somente rede `internal` |
| ocr-service | sem porta publicada | somente rede `internal` |

```bash
docker compose logs -f api worker       # logs estruturados, uma linha por evento, sem conteúdo documental
docker compose restart api worker       # os documentos continuam lá
docker compose down                     # para tudo e preserva os volumes
docker compose down -v                  # apaga também o banco e os originais enviados
```

Para expor o banco e o OCR em desenvolvimento: `docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build`.

> **Sem autenticação.** Qualquer pessoa com acesso à URL pode enviar, listar, visualizar, baixar e
> excluir documentos. Não publique na internet e use apenas documentos sintéticos, mascarados ou
> autorizados. `ALLOW_ANONYMOUS_ACCESS=false` fecha `/api/v1` com `503`, porque a PoC não tem
> provedor de autenticação para onde cair.

### Usar

1. **Pela interface:** abra http://localhost:3000, envie um arquivo (PDF, PNG, JPEG ou TIFF, até 25 MB e 50
   páginas) e acompanhe a tela de detalhe: tipo identificado, campos com valor lido, valor normalizado,
   confiança e validação, texto bruto por página e a linha do tempo do processamento.
2. **Pelo Swagger:** http://localhost:8080/swagger, `POST /api/v1/documents`, "Try it out", escolha o arquivo.
3. **Por curl:** ver [Exemplos com curl](#exemplos-com-curl).

Para experimentar, use os arquivos de [`samples/synthetic/documents`](samples/synthetic/documents), um de cada tipo.

```bash
curl -s "http://localhost:8080/api/v1/documents/$ID/text"     # texto bruto por página
curl -s "http://localhost:8080/api/v1/documents/$ID/result"   # tipo, campos, confiança e validação
curl -s -X POST "http://localhost:8080/api/v1/documents/$ID/reprocess"   # 202; 409 se já há job em andamento
```

### Configuração que importa

Todas em `.env` (ver `.env.example`, que explica cada uma):

| Variável | Padrão | Para quê |
|---|---|---|
| `OCR_MODEL_PROFILE` | `ppocrv5-mobile` | Modelo do OCR. `ppocrv6-small` e `ppocrv6-tiny` são 2 a 8× mais rápidos e menos exatos; **baixam os modelos na primeira subida** (ADR 0002) |
| `OCR_MEMORY_LIMIT` | `3g` | Teto de memória do `ocr-service` |
| `UPLOAD_MAX_SIZE_BYTES`, `UPLOAD_MAX_PAGE_COUNT` | 25 MB, 50 | Limites de upload |
| `QUEUE_MAX_ATTEMPTS` | `3` | Tentativas por documento antes de `FAILED` |
| `CLASSIFICATION_MIN_SCORE` | `0` | Pontuação mínima para aceitar um tipo. `0` usa o limiar de cada tipo (0,6); ver [Depurar uma classificação](#depurar-uma-classificação) |
| `ALLOW_ANONYMOUS_ACCESS` | `true` | `false` fecha a API |

### Depurar uma classificação

Um documento que saiu `UNKNOWN`, ou com o tipo errado, se explica em uma chamada. O endpoint refaz a decisão, com as
regras em vigor, sobre o texto da última extração e mostra ao lado o que foi gravado no processamento:

```bash
curl -s "http://localhost:8080/api/v1/documents/$ID/classification-diagnostics"                   # com o texto bruto
curl -s "http://localhost:8080/api/v1/documents/$ID/classification-diagnostics?includeText=false" # só a decisão
```

- `recorded`: tipo, confiança e versão das regras gravados quando o documento foi processado.
- `current`: a decisão das regras de agora e `reason`, a frase que a explica ("No type reached its threshold.
  Closest: BR_CIN with score 0.55 of 0.60"). Os dois diferem depois de uma mudança de regras e até o
  `POST .../reprocess`.
- `candidates`: todos os tipos tentados, do melhor para o pior. Cada um traz `score`, `threshold`, `accepted` e
  a lista completa de `evidence` e `counterEvidence`, achadas ou não, com o padrão que casou e se o OCR errou
  (`matchKind: FUZZY`, `edits`). O que faltou para o limiar está na `reason` do candidato.
- `pages`: o texto bruto do OCR, o mesmo de `/text`. `includeText=false` o omite.

A classificação soma pontos por evidência (cada tipo tem as suas, em `schemas/documents/*.json`), subtrai a
contra-evidência e aceita o tipo que atinge o limiar. Nenhum termo é obrigatório, o casamento tolera palavras
coladas e erros de letra do OCR, e o título sozinho não basta nos tipos ambíguos. Se o diagnóstico mostrar um
documento legítimo perto do limiar, há duas saídas: acrescentar o rótulo que faltou como padrão alternativo do
tipo (`DocumentTypeProfile.cs` e o schema, que um teste mantém iguais) ou baixar `CLASSIFICATION_MIN_SCORE` em
`.env` (afeta todos os tipos; reprocesse para aplicar).

**Latência.** Uma página A4 densa leva ~16 s de OCR com o contêiner sem limite de CPU e ~28 s limitado a 4 CPUs
(ADR 0002). O alvo do PRD (cinco páginas em 90 s) vale para o primeiro caso. Não imponha limite de CPU ao
`ocr-service` sem reler o ADR.

## Rodar os testes

Cinco suítes, todas em contêiner. Os comandos abaixo funcionam no bash e no PowerShell a partir da raiz do
repositório; no Windows, use `powershell -File scripts/dotnet.ps1 ...` no lugar de `bash scripts/dotnet.sh ...`.

| Suíte | Comando | O que cobre |
|---|---|---|
| Unitários (.NET) | `bash scripts/dotnet.sh test tests/unit/DocReader.UnitTests` | domínio, validadores, classificação, extratores (inclusive sobre OCR real capturado), processamento |
| Integração (.NET) | ver abaixo | fila e persistência em PostgreSQL real |
| OCR service | ver abaixo | API do serviço, páginas, perfis de modelo |
| Interface | ver abaixo | `tsc --noEmit` (o gate de qualidade do front) |
| Avaliador | ver abaixo | a ferramenta de avaliação |

```bash
# build com warnings como erros
bash scripts/dotnet.sh build DocReader.slnx

# integração: precisa do PostgreSQL do compose (docker compose up -d postgres) e da rede dele.
# Sem banco alcançável os testes são PULADOS, não falham: confira que rodaram (21 hoje).
docker run --rm --network docreader_internal -v "$PWD:/src" -v docreader-nuget:/root/.nuget/packages -w /src \
  -e "DOCREADER_TEST_CONNECTION=Host=postgres;Port=5432;Database=postgres;Username=docreader;Password=docreader" \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test tests/integration/DocReader.IntegrationTests

# ocr-service (usa um motor falso: não precisa do Paddle)
docker run --rm -v "$PWD/services/ocr-service:/work" -w /work python:3.12-slim sh -c \
  "pip install -q fastapi 'uvicorn[standard]' pydantic python-multipart pypdfium2 pillow numpy -r requirements-dev.txt \
   && python -m pytest -q"

# interface
docker run --rm -v "$PWD/apps/web-bff:/work" -w /work node:22-alpine sh -c "npm ci --no-audit --no-fund && npx tsc --noEmit"

# avaliador
docker run --rm -v "$PWD:/w" -w /w python:3.12-slim sh -c "pip install -q pytest && python -m pytest tests/accuracy -q"
```

O `dotnet test` usa xunit v3 sobre Microsoft.Testing.Platform (o `global.json` faz o opt-in).
`bash scripts/dotnet.sh run --project tests/unit/DocReader.UnitTests` executa a suíte direto.

### Teste ponta a ponta

Com o sistema de pé, envia um documento de cada tipo pela API, espera `COMPLETED` e confere cada campo contra o
`expected.json` da amostra e a tela de detalhe:

```bash
docker run --rm -v "$PWD:/w" -w /w python:3.12-slim python tests/e2e/stage3_acceptance.py \
  --api http://host.docker.internal:8080 --web http://host.docker.internal:3000
```

No Linux, acrescente `--add-host=host.docker.internal:host-gateway` ao `docker run`. Ver
[`tests/e2e/README.md`](tests/e2e/README.md).

## Avaliação

O framework mede o sistema contra **documentos reais anotados**: para cada arquivo de uma pasta local, compara a
extração com um ground truth em JSON e gera precision, recall e F1 por tipo e por campo, exatidão do dígito
verificador, acerto de classificação, rendimento de texto, latência e os três indicadores da PoC (PRD §3).

```bash
# 1. dataset FORA do repositório: /dados/avaliacao/cnh-001.jpg + cnh-001.truth.json, ...
# 2. sistema de pé, e então:
python scripts/evaluate.py --dataset /dados/avaliacao --api http://localhost:8080 --out /dados/relatorio
```

Sem Python local, o mesmo comando dentro do contêiner:

```bash
docker run --rm --add-host=host.docker.internal:host-gateway -v "$PWD:/w" -v /dados/avaliacao:/dataset:ro \
  -v /dados/relatorio:/report -w /w python:3.12-slim \
  python scripts/evaluate.py --dataset /dataset --api http://host.docker.internal:8080 --out /report
```

Teste de fumaça com as amostras sintéticas (o framework, o sistema e as amostras de acordo; F1 de 100%):

```bash
python scripts/evaluate.py --dataset samples/synthetic/documents --truth-suffix .expected.json \
  --api http://localhost:8080 --out .tmp/smoke --fail-on-indicators
```

O relatório sai em `report.json` e `report.md`. **Documento real nunca entra no repositório**: o script recusa uma
pasta de documentos dentro dele, o relatório não traz nome de arquivo nem valor de campo a menos que se peça, e os
documentos enviados são apagados da API ao fim. O formato do ground truth, a definição de cada métrica, a
comparação com uma rodada anterior (`--baseline`) e como montar um dataset estão em
[`docs/evaluation.md`](docs/evaluation.md).

## Amostras sintéticas

Nenhuma contém dado pessoal real. Os CPFs e o CNPJ são exemplos de manual (dígito verificador válido).

| Pasta | Gerador | Para quê |
|---|---|---|
| `samples/synthetic/` | `node scripts/make-samples.mjs` | assinatura de arquivo, contagem de páginas, limites (sem texto) |
| `samples/synthetic/ocr/` | `scripts/make-ocr-samples.py` | benchmark de OCR |
| `samples/synthetic/documents/` | `scripts/make-stage3-samples.py` | um documento de cada tipo, com `<amostra>.expected.json` |

Detalhes e comandos de geração em [`samples/synthetic/README.md`](samples/synthetic/README.md). Arquivos da raiz
da pasta, para os cenários de recusa:

| Arquivo | Para quê |
|---|---|
| `comprovante-1-pagina.pdf` | caminho feliz |
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

## Verificação manual de resiliência

```bash
docker compose restart                  # 1. persistência: os documentos e os originais continuam lá
curl -s "http://localhost:8080/api/v1/documents" | head -40
curl -s -o /dev/null -w "%{http_code}\n" "http://localhost:8080/api/v1/documents/$ID/content"   # 200

docker compose port postgres 5432       # 2. banco e OCR não expostos: sem mapeamento
docker compose port ocr-service 8000

docker compose stop ocr-service         # 3. OCR fora do ar no meio de um documento: ele volta para a fila com
                                        #    OCR_UNAVAILABLE, o original segue consultável, e ao religar
docker compose start ocr-service        #    (dentro de ~45 s) o documento conclui sozinho
```

## Desenvolvimento

O alvo é `net10.0`. Se a máquina não tiver o SDK 10.x, os helpers rodam o SDK em contêiner:

```bash
bash scripts/dotnet.sh build DocReader.slnx
bash scripts/dotnet.sh test tests/unit/DocReader.UnitTests
powershell -File scripts/dotnet.ps1 build DocReader.slnx     # equivalente no Windows
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
schemas/documents   JSON Schema, sinais de classificação e validações de cada tipo
tests/              unit, integration, e2e, accuracy (testes do avaliador)
deploy/docker       Dockerfiles (contexto de build = raiz)
docs/               PRD, ADRs, avaliação, contrato da API, evidência de benchmark
scripts/            geradores de amostra, avaliador, benchmarks, helpers de desenvolvimento
samples/synthetic   amostras geradas
```

## Documentação

| Documento | Conteúdo |
|---|---|
| [`docs/PRD.md`](docs/PRD.md) | Produto e arquitetura |
| [`docs/adr`](docs/adr) | Decisões: PostgreSQL como fila (0001); engine de OCR, latência e perfis de modelo (0002) |
| [`docs/evaluation.md`](docs/evaluation.md) | Ground truth, métricas e como avaliar |
| [`docs/bench`](docs/bench) | Medições de OCR: método, resultados e como reproduzir |
| [`docs/api`](docs/api) | Contrato exportado da API |
| [`schemas/documents`](schemas/documents) | Campos e validações por tipo |

## Licença e dados

PoC interna. Use somente documentos sintéticos, mascarados ou autorizados — nunca documentos
pessoais reais em ambiente compartilhado, e nunca no repositório.
