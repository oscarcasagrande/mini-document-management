# HANDOFF — Etapa 2 (OCR ponta a ponta)

Estado em 2026-09-24. Este arquivo é para quem retomar o trabalho, com ou sem contexto da conversa.
A fonte de verdade de produto continua sendo `docs/PRD.md`; as convenções, `CLAUDE.md`.

## Situação em uma frase

A Etapa 2 está **escrita, mas não compilada, não testada e não commitada**. O aceite ainda não rodou.
O bloqueio foi ambiental: o disco `C:` chegou a 0 bytes livres, o disco virtual do Docker Desktop
(`docker_data.vhdx`, ~42,5 GB) entrou em somente leitura, e sem Docker não há build .NET, testes,
compose nem aceite (`scripts/dotnet.sh` também roda em contêiner).

## Antes de qualquer coisa

1. Liberar espaço no `C:` (uns 15 GB). Conferir com `Get-PSDrive C`.
2. **Não apagar `docker_data.vhdx` nem resetar o Docker Desktop.** O `DOC-20260924-000003`, o original
   dele e todo o Postgres vivem nos volumes `docreader_postgres-data` e `docreader_document-storage`.
3. Reiniciar o Docker Desktop e confirmar que responde: `docker info` e `docker run --rm alpine touch /tmp/x`.
4. Limpar só o que é descartável: `docker builder prune`, o volume `ocr-onednn-venvs` e, se preciso,
   a imagem `docreader/ocr-bench` (reconstruível). Não usar `docker system prune --volumes`.
5. Conferir que os volumes de dados sobreviveram: `docker volume ls` e `docker compose up -d postgres`.

## O que está escrito e NÃO compilado

Nada abaixo passou por `dotnet build`, `dotnet test`, `pytest` ou `docker compose build`.
Exceção: um `dotnet build` anterior às últimas edições só acusou erros de nome, já corrigidos
(`Extraction` virou `DocumentExtraction`; XML doc de `ExtractionTextView`; fake `InMemoryDocumentStore`).

**ocr-service (Python)** — `services/ocr-service/`
- `app/settings.py`, `app/pages.py`, `app/engine.py`, `app/main.py`: FastAPI, `POST /v1/ocr/page`
  (uma página por chamada), PP-OCRv5 mobile + `latin_PP-OCRv5_mobile_rec`, `InferenceLimiter`
  (concorrência 1, timeout por página, o slot só é liberado quando a thread termina), problem+json,
  `X-Correlation-Id`, `/health` = readiness (503 até carregar o modelo), `/health/live`.
- `requirements.txt` (paddlepaddle **3.2.2**, paddlex[ocr-core] 3.7.2, paddleocr 3.7.0),
  `requirements-dev.txt`, `tests/` (`test_pages.py`, `test_api.py`, com engine falsa).
- `deploy/docker/Dockerfile.ocr-service`: modelos baixados e aquecidos no build, oneDNN ligado.
- A instalação `pip` do build chegou a terminar; o build caiu depois, no commit do cache (disco).

**Domínio / Application**
- `ProcessingJob`: `PagesCompleted`, `PageCount`; `locked_at` é o heartbeat.
- `Document`: `AdvanceTo`, `RecordProgress`, `RecordClassification`, `MarkCompleted`,
  `MarkRetryScheduled`, `MarkRequeued`; `MarkQueued(details)` zera `CompletedAt`.
- `Domain/Extractions/DocumentExtraction` e `ExtractedField`.
- `IDocumentOcrProvider` (com `IOcrProgress`), `IProcessingQueue` (`HeartbeatAsync`, `ReleaseAsync`,
  `Complete/Fail` por `ProcessingJob`, `JobFailureOutcome`), `IDocumentProcessingStore`,
  `IDocumentRepository` (resultado, texto, job mais recente, `QueueReprocessingAsync`).
- `Processing/DocumentProcessor`, `Classification/*` (regras `rules-1.0.0`), `DocumentReprocessingService`,
  consultas em `DocumentQueryService`, `OcrProviderOptions`, `ProcessingOptionsValidator`,
  `ProcessingQueueOptions` (`JobLockTimeout` 5 min, `ProcessingTimeout` 60 min).
- `BrCpfCardExtractor`: códigos de validação (`CHECK_DIGIT_VALID`, `DATE_VALID`, `NO_LABEL_NEARBY`...),
  `Version`, filtro de data de inscrição.

**Infrastructure**
- `PostgresProcessingQueue` reescrita (ver achados), `DocumentProcessingStore`, `DocumentRepository`
  (novas consultas, reprocessamento sob `FOR UPDATE`), `Ocr/PaddleOcrServiceProvider`,
  configurações EF de `extractions` e `extracted_fields`, índice único parcial
  `ux_processing_jobs_document_id_active`, `AddDocReaderOcrProvider`.
- **A migration ainda não foi gerada.** Comando:
  `bash scripts/dotnet.sh ef migrations add AddOcrResults --project src/DocReader.Infrastructure --startup-project apps/api`
  Conferir no SQL: tabelas `extractions` (jsonb em `page_texts`, `raw_ocr_result`, `structured_result`) e
  `extracted_fields`, colunas `pages_completed` e `page_count`, e o índice único parcial.

**Worker** — `apps/worker/ProcessingWorker.cs` (laço de consumo), `Program.cs` ligado.

**API** — `DocumentsController` (`/text`, `/result`, `/reprocess`), `DocumentResponseMapper`,
contratos novos (`DocumentTextResponse`, `DocumentResultResponse`, `DocumentProcessingResponse`,
links), `ApiExceptionHandler` (409 `RESULT_NOT_READY` e `REPROCESS_CONFLICT`), exemplos Swagger.

**Web/BFF** — página de detalhe com campos, texto bruto e progresso; `ReprocessButton`; rota
`/api/bff/documents/[id]/reprocess`; `DocumentStatusWatcher` com assinatura de progresso; CSS.
Gate do front: `npm run typecheck` em `apps/web-bff` (não rodou).

**Compose e ambiente** — `docker-compose.yml` (tags `stage2`, `mem_limit` 3g, concorrência 1, timeouts,
`QUEUE_*`), `.env.example`.

**Testes novos (não rodaram)**
- Unit: `DocumentProcessorTests`, `RulesDocumentClassifierTests` (inclui paridade com o schema JSON),
  `BrCpfCardExtractorOcrOrderTests`, `ProcessingOptionsValidatorTests`,
  `ResultQueriesAndReprocessTests`, `PaddleOcrServiceProviderTests`, `Fakes/ProcessingFakes.cs`.
- Integração (Postgres real): `PostgresProcessingQueueTests` reescrita (job longo com heartbeat não é
  pego por segundo worker; worker antigo perde a reserva; liberar sem gastar tentativa) e
  `ProcessingPersistenceTests` (resultado atômico, fencing, reprocessamento concorrente, índice único).
  `PostgresFixture` agora usa `EnableRetryOnFailure` e tem `ResetAsync`.
- Para rodar a integração é preciso publicar o Postgres:
  `docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d postgres`.

## Pendências, na ordem

1. Espaço em disco e Docker saudável (seção "Antes de qualquer coisa").
2. `bash scripts/dotnet.sh build DocReader.slnx` e corrigir o que aparecer (warnings são erros).
3. `bash scripts/dotnet.sh test tests/unit/DocReader.UnitTests`, depois os de integração.
4. Gerar e revisar a migration (comando acima).
5. `pytest` do ocr-service em contêiner Python (sem Paddle, os testes usam engine falsa) e
   `npm ci && npm run typecheck` em `apps/web-bff`.
6. `docker compose build` e `docker compose up -d`; conferir `docker inspect` do limite de memória do
   `ocr-service` (3 GiB) e `docker compose ps` todo healthy.
7. **Escrever `docs/adr/0002-*.md`** (ainda não existe), com:
   - escolha do PP-OCRv5 mobile e o texto reconhecido salvo em `docs/bench/ocr-benchmark-v5-mobile.json`;
   - números: cartões 6,9 s e 8,6 s (p50, oneDNN off, 3.3.1); A4 26–41 s; pico 2,09 GB; imagem de
     benchmark 2,57 GB; carga de modelo 6,2 s; exatidão de CPF, nome e nascimento nos dois cartões;
   - PP-StructureV3 medido só em 2 cartões (~28 s), OOM (6 GiB) nas três páginas ≥ 1,17 MP, tabelas
     nunca demonstradas; fora da Etapa 2, entra na Etapa 3 configurável por tipo documental;
   - **páginas A4 (26–49 s) NÃO atendem o RNF de 5 páginas em 90 s**: pendência obrigatória antes do
     contrato social (Etapa 3), com oneDNN, PP-OCRv6 e redução de resolução como hipóteses a medir;
   - experimento oneDNN: 3.3.1 falha (`ConvertPirAttribute2RuntimeAttribute`), 3.2.2 funciona, texto e
     confiança idênticos; controle 3.2.2 sem oneDNN → com oneDNN: cartão limpo 8,5→5,6 s, escaneado
     6,9→4,9 s, A4 tabela 29,5→22,7 s (uma rodada exploratória deu 18,1 s), A4 texto denso 47,2→31,0 s;
     pico 2,07→2,28 GB; adotado, com `OCR_ENABLE_MKLDNN=false` para reverter;
   - heartbeat: `locked_at` renovado por página, recuperação mede o silêncio, fencing por tentativa;
   - o que ficou de fora: PDF com camada de texto nativa (RF-009), orientação/deskew.
   Arquivos de evidência em `docs/bench/`: `ocr-benchmark-v5-mobile.json`,
   `ocr-onednn-pp3.2.2-mkldnn-false.json`, `ocr-onednn-pp3.2.2-mkldnn-true.json`. Atualizar também a
   tabela de `docs/bench/README.md`, que ainda traz os números da primeira rodada.
8. Atualizar `CLAUDE.md` (seção "Etapa atual"), `README.md`, `schemas/documents/README.md` se preciso, e
   `docs/api/README.md` com os endpoints novos.
9. Rodar o aceite (seção abaixo).
10. Auditoria, commit e push (seção "Regra de commit").

## Achados já corrigidos no código (não repetir)

- **Data errada no cartão limpo:** o PP-OCRv5 devolve "INSCRICAO EM 02/09/2003" entre o rótulo
  NASCIMENTO e o valor; o extrator lia 2003 como nascimento. Corrigido com `NonBirthDateMarkers`;
  teste em `BrCpfCardExtractorOcrOrderTests`. A fixture antiga de `BrCpfCardExtractorTests` diz ser da
  amostra limpa, mas tem a ordem da escaneada.
- **Transação explícita na reserva do job:** `AcquireNextAsync` abria `BeginTransaction` fora de
  `strategy.ExecuteAsync`, o que lança com `EnableRetryOnFailure` (o que produção usa). Agora é uma
  instrução só, atômica; as demais transações passam pela estratégia. O fixture de teste agora também
  usa retry, para esse erro não voltar a passar despercebido.
- **Fencing:** `Heartbeat`, `Complete`, `Fail` e `Release` só valem para a tentativa que ainda é dona
  do job (`attempt_count`). Um worker que perdeu a reserva não sobrescreve o que assumiu.
- **Estado do documento na recuperação de job preso:** a SQL de recuperação também devolve o documento a
  `QUEUED` e registra o evento `RETRY_SCHEDULED` com `STUCK_JOB_RECOVERED`.
- **Falha transitória:** o documento volta a `QUEUED`, mantém o erro visível e a UI mostra a nova tentativa.
- **Bug do benchmark:** a variável do laço sobrescrevia o nome da pipeline no JSON; corrigido, JSON regerado.
- **Conflito de nome:** entidade `Extraction` colidia com o namespace `DocReader.Application.Extraction`;
  virou `DocumentExtraction` (tabela continua `extractions`).
- **Shell:** heredoc com `$"..."` (interpolação C#) quebra o Bash tool; usar a ferramenta de escrita ou
  `node arquivo.js`.

## Aceite da Etapa 2

Tudo via `docker compose`, com o build feito de verdade (não vale rodar só os testes).

1. Testes verdes: unit e integração (.NET), `pytest` do ocr-service, `npm run typecheck`.
2. `docker compose up -d --build`: todos os serviços healthy; `ocr-service` com limite de 3 GiB.
3. **Reprocessar `DOC-20260924-000003` (cartão de CPF):** `POST /api/v1/documents/{id}/reprocess`
   (ou deixar o job pendente que já existe ser consumido). Ele deve ir de `QUEUED` a `COMPLETED`,
   passando por `PREPROCESSING`, `OCR_RUNNING`, `CLASSIFYING`, `EXTRACTING`.
4. `GET /text` devolve o texto bruto por página; `GET /result` devolve os campos do `BR_CPF_CARD`
   (cpf, name, birthDate) com confiança, `validationStatus` e `CHECK_DIGIT_VALID` no CPF. Conferir na tela
   de detalhe (`http://localhost:3000/documents/{id}`), de preferência com captura.
   Esperado para a amostra sintética: CPF `11144477735`, nome `MARIA APARECIDA DA SILVA SOUZA`,
   nascimento `1985-03-14`.
5. **Derrubar o ocr-service no meio:** reprocessar, esperar `OCR_RUNNING`, rodar
   `docker compose stop ocr-service`. Verificar: `GET /content` do original continua respondendo 200;
   o job vira retry (`RETRY_SCHEDULED`, documento volta a `QUEUED`, erro `OCR_UNAVAILABLE` visível);
   `docker compose start ocr-service` dentro de ~45 s (backoff 15 s + 30 s; são 3 tentativas) e o
   documento chega a `COMPLETED` sozinho. Repetir o retry pelo menos uma vez fora do prazo para ver o
   `FAILED` definitivo e o reprocessamento manual funcionando.
6. Conferir nos logs de todos os serviços que nenhum CPF, nome ou texto do documento aparece.

Se qualquer item falhar: **não commitar**; registrar o que falhou e parar.

## Regra de commit

Só depois do aceite validado via `docker compose`:

1. **Auditoria de arquivos**, como no primeiro commit: `git status` e `git diff --stat`; listar tudo que
   entra; confirmar que não há segredos, `.env`, dumps, PDFs ou imagens de documentos fora de
   `samples/synthetic/`, nem `bin/`, `obj/`, `node_modules/`, `.next/`, logs ou saídas temporárias.
   `scripts/ocr_onednn_experiment.sh` e os JSONs de `docs/bench/` entram (são evidência).
2. **Commit novo em `main`, sem `--amend`**, com mensagem descritiva da Etapa 2 (em inglês, como o
   histórico), citando o ADR 0002 e a pendência de latência em páginas A4 (26–49 s contra o RNF de
   5 páginas em 90 s). Terminar com a linha de coautoria definida na sessão.
3. `git push origin main`, e mostrar ao usuário o hash e o resumo.
