# CLAUDE.md — DocReader PoC

Contexto para agentes que trabalham neste repositório. A fonte de verdade de produto e
arquitetura é `docs/PRD.md`; este arquivo resume o que precisa estar na cabeça antes de editar código.

## O que é

PoC local de ingestão, OCR, classificação e extração de documentos brasileiros, executada
inteiramente por `docker compose up`. Sem cloud, sem API proprietária, sem autenticação.

Plano de execução em etapas (PRD §26).

**Etapa 1 — concluída e aprovada.** Compose completo, upload pela API, persistência, lista,
visualização/download e Swagger.

**Etapa 2 — concluída.** OCR ponta a ponta (PaddleOCR PP-OCRv5 mobile em CPU), fila com heartbeat,
retry e fencing, `/text`, `/result`, `/reprocess`. Decisão de engine e números em `docs/adr/0002-*.md`.

**Etapa 3 — concluída.** Extração estruturada de sete tipos: `BR_CPF_CARD`, `BR_CIN` (CIN e RG, com MRZ
TD1), `BR_CNH`, `BR_PROOF_OF_ADDRESS`, `BR_CNPJ_CARD`, `BR_CCMEI` e `BR_SOCIAL_CONTRACT`. Cada um tem schema
em `schemas/documents/` (o README de lá lista campos, validações e o que **não** é validado), perfil de
classificação, extrator e testes. A extração é por rótulo e geometria (`LineSearch`, `FieldReaders` em
`src/DocReader.Application/Extraction`); o contrato social é por expressões sobre o texto juntado
(`ProseIndex`), com sócios como `partners[N].*`.

**Etapa 4 — avaliação e hardening: framework pronto, medição em documento real pendente.** Aguarda aprovação.

**Etapa 5 — calibração dos extratores contra OCR real: feita para CNH e RIC/RG; os outros cinco tipos não foram
medidos em documento real.**

- `GET /api/v1/documents/{id}/extraction-diagnostics` refaz a extração sobre os blocos de OCR gravados e explica cada
  campo (motivo, rótulos tentados, candidatos, cobertura). Os blocos normalizados vão em `raw_ocr_result` (`blocks`, ao
  lado do `raw` do provedor); extração anterior a isso cai em `PAGE_TEXT`, sem coordenadas. O rastro (`ExtractionTrace`)
  é opcional e o `LineSearch` o alimenta por escopo de campo; CPF e contrato social não passam por ele.
- Casamento de rótulo tolerante (`LabelKeys`, `MatchTolerant` no `LineSearch`), bloco vertical ignorado como valor,
  `Following` que não atravessa rótulo, data com espaço, nome em várias linhas, blocos da mesma linha visual juntos
  (`RowMerger`) e "<" da MRZ perdido no fim reposto. Extratores subiram para `1.1.0`.
- Medido em documento real (dois exemplares, um por tipo): CNH 4/8 → 7/8, RIC 2/10 → 10/10 campos lidos. A categoria da
  CNH é uma letra que o OCR não lê. Fixtures `cnh-real` e `ric-real` em `Fixtures/ocr`, mascaradas, com
  `RealDocumentExtractionTests`.

- **Classificação por evidência** (`rules-2.0.0`): cada tipo soma o peso das evidências achadas, subtrai a
  contra-evidência e é aceito no limiar (0,6; `DocReader:Classification:MinimumScore` / `CLASSIFICATION_MIN_SCORE`
  o substitui para todos). Nenhuma frase é obrigatória; `SearchableText` casa sem acento e caixa, com palavras
  coladas ou partidas e com um erro de letra a cada oito. Os pesos vivem em `DocumentTypeProfile` e no bloco
  `x-docreader.classification` de cada schema, e um teste exige que sejam iguais. Nasceu de documento real: o
  PP-OCRv5 devolve `REPUBLICAFEDERATIVADOBRASIL` e o RG antigo é "REGISTRO DE IDENTIDADE CIVIL" (RIC), que cai em
  `BR_CIN`.
- **Diagnóstico**: `GET /api/v1/documents/{id}/classification-diagnostics` refaz a decisão com as regras de agora
  (mesmo código do `Classify`) e devolve texto, pontuação por tipo, evidência achada e faltante e a razão. Sempre
  o primeiro passo diante de um `UNKNOWN`. Não grava nada e não loga conteúdo.

- DV do número de registro da CNH (`CnhRegistration`, algoritmo do DENATRAN como o `brdoc` o reproduz) e
  sinalização de validade vencida (`DOCUMENT_EXPIRED`, sem reprovar o campo) em CIN e CNH.
- `scripts/evaluate.py` avalia o sistema contra uma pasta **fora do repositório** de documentos anotados:
  precision, recall e F1 por tipo e campo, exatidão de DV, classificação, texto utilizável, latência e os três
  indicadores do PRD §3. Formato do ground truth e métricas em `docs/evaluation.md`; testes em `tests/accuracy`.
  Recusa pasta dentro do repo, não escreve valor de campo no relatório sem `--include-values`, apaga da API o
  que enviou. Smoke test com `samples/synthetic/documents` (`--truth-suffix .expected.json`).
- Testes (na etapa 4): as sete amostras rodam sobre **OCR real** capturado em
  `tests/unit/DocReader.UnitTests/Fixtures/ocr`; 49 do avaliador. Contagens do `pytest` do OCR e do .NET na seção acima.
  Ponta a ponta: `tests/e2e/stage3_acceptance.py`.
- Latência de A4 fechada no ADR 0002: o alvo (≤ 18 s/página) é atingido **sem limite de CPU** (pior caso
  15,8 s) e não com 4 CPUs (28 s). `OCR_MODEL_PROFILE` troca o modelo; os menores são mais rápidos e menos
  exatos, e não foram adotados.

**Funcionalidades de produção (pós-etapa 4).** Cinco itens sobre o pipeline, todos com migration, testes e tela no BFF:

- **ProductService** (`Domain/Catalog`): código único em maiúsculas; o upload aceita `productServiceCode` (desconhecido ou
  inativo = 422). `/api/v1/product-services`.
- **RetentionPolicy** (`Domain/Retention`): escopo tipo/produto; precedência tipo+produto > produto > tipo > global. Uma global
  (id fixo `…0001`, 365 dias, semeada na migration) que não se apaga. `expiresAt` é calculado no upload, na classificação
  e no reprocessamento; **mudar a política não recalcula** o que já existe. `PurgeExpiredDocumentsJob` no worker
  (`PURGE_SCHEDULE_CRON`, Cronos, UTC) apaga de documentos em estado final vencidos o **arquivo**, o **texto do OCR** e os **campos extraídos**
  (`extractions` inteira, com `extracted_fields` em cascata, na mesma transação que marca `PURGED`; `PurgedContent` nomeia
  o que saiu no evento `PURGED`: `reason=RETENTION_EXPIRED deleted=file,ocr_text,extracted_fields`). O documento, a linha do
  tempo e os jobs ficam como tombstone. `/content`, `/text`, `/result`, `/reprocess` e os `*-diagnostics` dão 410
  (`EnsureNotPurged` no `DocumentQueryService`); o `GET` do documento continua 200 com `PURGED`.
- **StorageRepository** (`Domain/Storage`): `IFileStorage` virou fachada que escolhe o adaptador pelo `repositoryId` do documento
  (herdado do produto ou do padrão). Os quatro provedores são implementados: FileSystem, Database (`document_blobs`),
  AzureBlobStorage e AwsS3 — ver a seção de armazenamento em nuvem abaixo. A configuração é cifrada por `ISecretProtector`
  (AES-256-GCM, chave em `STORAGE_CONFIG_ENCRYPTION_KEY`) e nunca sai numa resposta. Exatamente um repositório padrão
  (índice único parcial).
- **Webhooks** (`Domain/Webhooks`, `Application/Webhooks`): outbox transacional (`webhook_deliveries`, payload em `text`, **não**
  `jsonb`, para os bytes assinados não mudarem). O `WebhookDispatcher` do worker reivindica com `FOR UPDATE SKIP LOCKED`,
  com fencing; assinatura `sha256=<hex>` sobre o corpo exato; 4 tentativas (10 s/30 s/90 s); falha final vira o evento
  `WEBHOOK_DELIVERY_FAILED` no documento. Bloqueio de rede privada por padrão, no cadastro e no `ConnectCallback`
  (`WEBHOOK_ALLOW_PRIVATE_NETWORKS`).
- **Referência externa**: `GET /api/v1/documents/by-external-reference/{reference}` devolve o documento mais recente (exato, com
  diferença de caixa) ou 404; a lista filtra por `externalReference` (parcial). Índice parcial em `external_reference`.
- **Exclusão LGPD/GDPR** (`Domain/GdprDeletion`, `Application/GdprDeletion`): `DELETE /api/v1/documents/{id}` continua a
  limpeza rápida e incondicional de sempre (RF-014). Ao lado dela, `DELETE /api/v1/documents/{id}/gdpr-delete` não apaga
  nada: cria um `GdprDeletionRequest` `PENDING` (409 se o documento está vinculado a um `ProductService` ativo, 400 se
  `expiresAt` ainda não venceu). `POST /api/v1/gdpr-deletion-requests/{requestId}/approve` (**TODO(RBAC)**, mesmo estilo do
  endpoint de revelar configuração) ou `.../reject` decide o pedido; sem decisão em `GDPR_AUTO_APPROVE_AFTER_HOURS` horas
  (padrão 24, `DocReader:GdprDeletion:AutoApproveAfterHours`), o `GdprDeletionWorker` aprova sozinho
  (`approvedBy=system:auto-approve-24h`, distinguível de um operador real). Aprovado, o mesmo worker reivindica com
  `FOR UPDATE` (lock-e-reconfere, o idioma de `MarkPurgedAsync`/`RetentionReapplyRequestRepository`) e executa: apaga o
  arquivo primeiro (nunca grava "apagado" antes de apagar de verdade), depois o texto do OCR e os campos extraídos — a
  extração mesma que a rotina de expurgo apaga, via `ExtractionCleanup` compartilhado entre as duas. O documento reaproveita
  o status `PURGED` (mesmo tombstone, mesmo 410 em `/content`, `/text`, `/result`, `/reprocess` e os `*-diagnostics` via
  `EnsureNotPurged`), mas grava `GDPR_DELETION_EXECUTED` na linha do tempo em vez de `PURGED`, com `reason=GDPR_REQUEST` e o
  id do pedido — por isso as duas origens continuam distinguíveis apesar do status compartilhado. Cada transição
  (`GDPR_DELETION_REQUESTED/APPROVED/REJECTED/EXECUTED`) grava também um `AuditLog`. `GET
  /api/v1/documents/{id}/gdpr-deletion-requests` lista os pedidos de um documento; `GET
  /api/v1/gdpr-deletion-requests/{requestId}` busca um pelo id.

**Configuração Dinâmica.** Três itens que tiram classificação e extração do código:

- **DocumentType** (`Domain/Catalog`): `code`/`name`/`schema`/`classificationRules`/`extractionRules`/`active`/`isBuiltIn`.
  CRUD em `/api/v1/document-types`; os sete tipos de sempre migraram do código para a tabela como seed da migration
  (`classificationRules` gerado de `DocumentTypeProfile.All`, `schema` copiado de `schemas/documents/*.json`, embutido em
  `Migrations/Seeds` — a migration não depende de arquivo fora do assembly). `IsBuiltIn` impede exclusão (409
  `DOCUMENT_TYPE_BUILT_IN_PROTECTED`; desative em vez de apagar), mas `PUT` edita as regras de um tipo embutido normalmente.
  A **classificação** consulta a tabela a cada chamada (`DynamicDocumentClassifier`, `IDocumentClassifier` agora assíncrono),
  não mais `DocumentTypeProfile.All` em memória: uma regra editada por `PUT` vale no próximo documento, sem deploy nem
  reinício. `RulesDocumentClassifier` (o motor de pontuação puro, síncrono) e seus testes continuam intactos — é só quem o
  alimenta com perfis que mudou. A **extração** continua por classe C# por tipo (`IDocumentExtractor`); `extractionRules` é
  hoje só metadado, não interpretado em runtime.
- **Recálculo de retenção**: `PUT /api/v1/retention-policies/{id}/reapply-to-existing` enfileira um
  `RetentionReapplyRequest` (`Pending`) e devolve 202; `RetentionReapplyWorker` no worker reivindica pedidos pendentes
  (`FOR UPDATE SKIP LOCKED`, poll de 2 s) e recalcula `expiresAt` dos documentos daquela política em lotes de 100
  (`Document.ReapplyRetention`, que preserva o início do ciclo e só troca a duração), registrando `RETENTION_POLICY_REAPPLIED`
  em cada um. Idempotente: um documento já no valor novo não é tocado de novo.
- **Reclassificação**: `PUT /api/v1/documents/{id}/reclassify-and-extract` enfileira um novo job com as regras de tipo
  documental atuais, do mesmo jeito que `/reprocess` (preserva o documento e as extrações anteriores), mas grava também o
  evento `RECLASSIFICATION_TRIGGERED`. O tipo e os campos gravados mudam se a classificação ou a extração mudarem quando o
  worker processar o job — não há lógica nova de pipeline, só o gatilho.
- Tela de cadastro de tipos documentais no BFF (`/config/document-types`), no mesmo componente genérico das outras telas de
  configuração; ganhou um tipo de campo `"json"` (textarea visível e sempre enviado, ao contrário do `"secretJson"` mascarado
  e opcional que as outras telas usavam).
- Testes: 934 unitários, 95 de integração (rodam só com PostgreSQL alcançável: senão são pulados, confira o total).

**Pré-processamento de OCR (RF-009).** Três capacidades, todas no `ocr-service`; o contrato de `POST /v1/ocr/page`
ganhou `hasNativeTextLayer`, `rotationDegrees`, `deskewed` e `processedWithStructure` por página, agregados em
`DocumentExtraction` (`has_native_text_layer`, `rotation_degrees`, `deskewed`, `ocr_processed_with_structure`) e em
quatro eventos novos (`TEXT_EXTRACTED_FROM_PDF_NATIVE_LAYER`, `DOCUMENT_ROTATED`, `DOCUMENT_DESKEWED`,
`OCR_REPROCESSED_WITH_PP_STRUCTUREV3`):

- **Camada de texto nativa** (`app/native_text.py`): uma página de PDF com pelo menos `OCR_PDF_NATIVE_TEXT_MIN_CHARS`
  (20) letras e dígitos é lida por `pdfplumber`, sem rasterizar nem chamar o Paddle (~100-200 ms contra ~5 s/página);
  os blocos vêm das linhas do pdfplumber, na mesma escala de pixel que a rasterização usaria, para não quebrar
  `LineSearch`. Camada vazia, só numeração de página, lixo de `(cid:N)` ou página coberta por imagem caem para OCR.
- **Rotação e deskew** (`app/preprocess.py`, `OCR_ORIENTATION_CORRECTION`): perfil de projeção por variância decide
  entre 0/90/180/270° (a direção vem de ascendentes/descendentes das letras e do alinhamento da margem, não só da
  variância, que empata 0° com 180°); Hough (`cv2.HoughLinesP`) corrige inclinação fina entre
  `OCR_DESKEW_MIN_DEGREES` (0,5°) e `OCR_DESKEW_MAX_DEGREES` (20°). Roda em toda página que vai a OCR (rasterizada de
  PDF ou imagem), antes do Paddle. Grade de validação de 300 casos: 276 corretos, nenhuma página já correta foi
  girada (as 24 divergências são abstenções em página degradada ou sem caixa baixa, nunca um giro errado).
- **PP-StructureV3 sob demanda** (`app/structure.py`, `app/tables.py`, `OCR_USE_PP_STRUCTUREV3_FOR_TABLES`, padrão
  `false`): um heurístico puro sobre os blocos do v5 (linhas por centro Y, colunas reaproveitadas por várias linhas)
  decide se a página parece tabela; se a confiança média da grade estiver abaixo de
  `OCR_STRUCTURE_TABLE_CONFIDENCE_THRESHOLD` (0,80) e a página estiver dentro de `OCR_STRUCTURE_MAX_MEGAPIXELS` (4,0,
  medido — não os 1,17 MP do ADR 0002 original, que media os modelos padrão do PP-StructureV3, maiores que o
  detector/reconhecedor mobile que este serviço já usa), o Paddle roda de novo com PP-StructureV3, **em processo
  filho, com oneDNN desligado** (ligado corrompe a heap) **e primeiro candidato do OOM killer** (`oom_score_adj`):
  se o filho for morto, a página fica com o resultado do v5 e um aviso no log, sem derrubar o serviço. Pesos (~873 MB)
  não vão na imagem; baixam sob demanda para `OCR_MODEL_CACHE_DIR` (`/health` expõe `structureEnabled`/`structureReady`).
  **Ligar exige `OCR_MEMORY_LIMIT=6g`** (documentado no compose; o padrão do serviço continua 3g com a opção desligada).
  Medido: **PP-StructureV3 piorou a leitura de tabela** (55→52, 56→54 e 3→0 campos exatos de 57 valores conhecidos,
  substituindo os blocos do v5 pelos dele) — por isso o padrão é desligado; ver o adendo de 2026-09-29 do ADR 0002.
- Testes: 92 do `pytest` do OCR (1 pulado fora do container), mais os unitários do .NET acima.

**O que não foi feito, e por quê:** nenhuma medição em documento real (não há dataset; o framework existe para
isso); a comparação opcional com Tesseract/Docling do PRD §26; e a decisão de continuidade e produção, que
depende da medição. As amostras sintéticas provam que o pipeline funciona e que as regras não regridem, não
que os extratores acertam em documento de outro estado, concessionária ou junta. PP-StructureV3 funciona mas piora
a exatidão de tabela medida; fica desligado por padrão até um gatilho de revisão do ADR 0002 mudar isso.

**Armazenamento em nuvem, backup/restore e migração entre repositórios.** Três itens sobre `StorageRepository`:

- **Adaptadores Azure Blob e AWS S3** (`Infrastructure/Storage`): implementam `IStorageAdapter` com a mesma chave
  `documents/yyyy/MM/dd/{id}/original{ext}` do adaptador de filesystem (`StorageKeyLayout`, compartilhada pelos três).
  `connectionConfig` esperado, cifrado como qualquer outro repositório (`STORAGE_CONFIG_ENCRYPTION_KEY`):
  **Azure** — `connectionString` e `container` (ambos obrigatórios); **S3** — `bucket`, `accessKeyId` e `secretAccessKey`
  (obrigatórios), `region` e `serviceUrl` (opcionais — `serviceUrl` com `ForcePathStyle` também atende MinIO e outros
  serviços compatíveis com S3). `StorageRepository.IsProviderImplemented` agora aceita os quatro provedores: Azure e S3
  podem ser o repositório padrão. Testado de ponta a ponta (upload de 1 MB, download, exclusão) contra um Azurite real;
  não havia um emulador de S3 alcançável neste ambiente (registry restrito), então o adaptador AWS tem só testes
  unitários de construção/validação — mesmo formato de código do adaptador Azure.
- **Backup e restore** (`Application/Backup`, `Domain/Backup`, `Infrastructure/Backup`): `POST /api/v1/admin/backup`
  (repositório de destino opcional, padrão o repositório padrão do sistema; nunca um repositório `DATABASE`, 422 —
  o backup ficaria dentro do banco que ele protege) enfileira um `BackupJob`; o worker roda `pg_dump --format=plain
  --clean --if-exists` num snapshot consistente com o manifesto de documentos, empacota `data.sql` +
  `storage_manifest.json` + os arquivos dos repositórios locais (FileSystem/Database — nuvem entra só como metadado no
  manifesto, o blob não é baixado) + `checksums.sha256` num `.tar.gz` (`System.Formats.Tar`, sem depender de um binário
  `tar`), assina a lista de checksums com uma chave derivada de `STORAGE_CONFIG_ENCRYPTION_KEY` (`checksums.sha256.hmac`
  — sem isso, qualquer um monta um `.tar.gz` com SQL próprio e o SHA-256 bate) e grava o arquivo por `IFileStorage`
  (mesmo mecanismo dos documentos, por isso "configurável para S3/Azure" não precisou de código novo). `GET
  /api/v1/admin/backup/{id}` mostra o status; `GET .../{id}/content` baixa o `.tar.gz`. `POST /api/v1/admin/restore`
  recebe o `.tar.gz` (multipart, até 2 GiB), valida checksum e assinatura **na hora** (400 se inválido, nada é
  enfileirado) e só então cria um `RestoreJob` assíncrono. O worker liga um portão de somente-leitura em nível de
  aplicação (`SystemState`, uma linha fixa; `ReadOnlyGateMiddleware` recusa POST/PUT/PATCH/DELETE em `/api` com 503
  `SYSTEM_READ_ONLY` enquanto está ligado — leituras e o próprio endpoint de restore continuam liberados) e roda `psql
  --single-transaction --set ON_ERROR_STOP=on -f data.sql`: qualquer erro faz o Postgres reverter a restauração
  inteira, então "rollback" aqui é uma transação de verdade, não um remendo da aplicação. Confirmado com um teste real
  de arquivo malicioso (SQL que teria apagado `documents`) e com `docker stop` no worker no meio de uma restauração —
  os dois casos voltam o banco ao estado anterior e derrubam o portão no `finally`. Os arquivos são escritos de volta
  reconstruindo a chave determinística a partir de `documentId`/extensão/data de upload; se não bater com a chave
  gravada no manifesto (documento migrado de repositório, por exemplo), esse documento entra como falha em vez de
  arriscar apontar para o arquivo errado. `DocReader:Backup` configura `WorkingDirectory` (pasta de rascunho, padrão
  `docreader-backup` sob o temp do sistema), `PgDumpPath`/`PsqlPath` (nome no PATH ou caminho absoluto), `CommandTimeout`
  (padrão 2 h) e `MaxExtractedBytes` (teto do que uma restauração extrai de um arquivo, padrão 20 GiB). A imagem do
  worker precisou do `postgresql-client-17` do repositório apt do próprio PostgreSQL (PGDG): a base Ubuntu 24.04 só
  tem a versão 16 por padrão, incompatível com o `postgres:17-alpine` do compose.
  **Risco de segurança, não resolvido nesta etapa:** os endpoints de admin são anônimos como o resto da PoC
  (`ALLOW_ANONYMOUS_ACCESS`), mas aqui o preço é maior — `/backup` baixa um dump com dado pessoal de todo documento, e
  `/restore` roda SQL arbitrário como o papel do banco da aplicação, que no compose é superusuário. A assinatura
  impede um arquivo forjado sem a chave, mas não autentica quem chama o endpoint. Não expor além de localhost sem
  colocar autenticação na frente. O portão de somente-leitura também cobre só a API: os outros jobs do worker
  (processamento, expurgo, webhooks, `RetentionReapplyWorker`, `StorageMigrationWorker`) continuam rodando durante
  uma restauração.
- **Migração entre repositórios** (`Application/StorageMigrations`, `Domain/StorageMigrations`): `POST
  /api/v1/admin/storage-migration` (`sourceRepositoryId`, `targetRepositoryId`, filtro opcional por tipo/produto/data)
  enfileira um `StorageMigrationJob`; o `StorageMigrationWorker` reivindica com `FOR UPDATE SKIP LOCKED` (mesmo padrão
  do `RetentionReapplyWorker`) e move documentos em lotes de 10, cada um em sua própria transação: lê do repositório de
  origem por `IFileStorage`, grava no destino, atualiza `storageRepositoryId`/`storageKey`
  (`Document.MigrateStorageRepository`, que tirou `StorageKey`/`StorageRepositoryId` de `private init`) e registra
  `STORAGE_MIGRATED` na linha do tempo — **nunca apaga do repositório de origem**. `migrationHistory` na resposta do
  documento é derivado desses eventos, não é coluna nova. `DELETE
  /api/v1/admin/storage-migration/{jobId}/rollback` só funciona em job `Pending`/`Running` (409 se já terminou),
  interrompe cooperativamente antes do próximo lote e não desfaz o que já foi movido. Testado ao vivo: 55 de 56
  documentos reais migrados de FileSystem para um repositório Database (o restante já estava em outro repositório),
  conteúdo continuou acessível depois.
- **Revelar a configuração de um repositório**: `GET /api/v1/storage-repositories/{id}/connection-config` decifra e devolve a
  configuração completa (`Cache-Control: no-store`), para a tela de edição poder mostrá-la sob demanda em vez de reenviar tudo
  às cegas. O olhinho da tela de repositórios busca esse endpoint na primeira vez que é aberto; ocultar de novo esquece o valor
  (limpa da tela, não só mascara), e salvar sem revelar/editar mantém o que já estava gravado (atualização parcial de sempre).
  Cada chamada grava um `AuditLog` (`STORAGE_CONFIG_REVEALED`, quem, quando, nunca o valor). **TODO(RBAC)**: hoje esse endpoint
  é anônimo como o resto da PoC — restringir ao papel `docreader-admin` quando OIDC/RBAC existir (mesmo TODO no código, em
  `StorageRepositoriesController.GetConnectionConfigAsync` e `StorageRepositoryService.RevealConnectionConfigAsync`).
- **AuditLog** (`Domain/Audit`, tabela `audit_logs`): registro append-only e genérico — quem (`userId`, nulo hoje porque a PoC
  não tem autenticação), ação, tipo e id do recurso, quando, IP, user agent e `changes` (metadado do que mudou, nunca conteúdo
  ou segredo). `AuditActionTypes` (`Domain/Audit`) centraliza os nomes de ação, no mesmo espírito do `DocumentEventTypes`.
  Além do `GET .../connection-config` (que grava o seu próprio `STORAGE_CONFIG_REVEALED` na mão), um filtro genérico de ação
  (`AuditedAttribute`, `IAsyncActionFilter` em `apps/api/Audit`) audita toda rota mutante que o decora — upload, reprocessamento,
  reclassificação e exclusão de documento; criação/edição/exclusão de política de retenção e o gatilho de reaplicação; criação/
  edição/exclusão de repositório de armazenamento, produto/serviço, tipo documental e assinatura de webhook; início de backup,
  restore e migração de armazenamento, e o rollback desta última — só em resposta `2xx`. A decisão de **o quê** gravar
  (`resourceId` da rota `id`/`jobId` ou, na falta dela, da propriedade `Id` do corpo da resposta; `changes` num `PUT` como os
  nomes de propriedade que o corpo da requisição carregou, nunca o valor) é `AuditDecision` (`Application/Audit`), uma função
  pura sem tipo do ASP.NET Core, testada sem subir o pipeline MVC. `GET /api/v1/audit-logs` (`AuditLogsController`) lista com
  filtro por `userId`, `action` e intervalo de `occurredAt`, paginado como todo outro `PagedResponse`; mesmo `TODO(RBAC)` do
  endpoint de revelar configuração. Tela somente leitura no BFF em `/config/audit-logs`, fora do `ConfigAdmin` genérico (que é
  de criar/editar/excluir): filtro por usuário/ação/data e paginação, no mesmo estilo de `/documents`.
- **Cifra dos campos extraídos em repouso**: `raw_value` e `normalized_value` de `extracted_fields` (o CPF, nome, endereço etc.
  que o OCR/extração leram do documento) são cifrados por `IFieldEncryptionProtector` (AES-256-GCM, mesmo formato de envelope
  de `ISecretProtector`, compartilhado em `AesGcmEnvelope` para as duas implementações não divergirem), chave em
  `EXTRACTED_FIELD_ENCRYPTION_KEY` — **separada** de `STORAGE_CONFIG_ENCRYPTION_KEY` de propósito, para girar uma sem a outra.
  A cifra é transparente: um `ValueConverter` no EF Core (`ExtractedFieldConfiguration`) cifra ao gravar e decifra ao ler, sem
  mudar nada em código que use `ExtractedField.RawValue`/`NormalizedValue`; a coluna cresceu de `varchar(2048)` para
  `varchar(10000)` porque o envelope (JSON com nonce, tag e o texto em base64) é maior que o texto puro. O `ValueComparer` do
  conversor compara o texto puro (não o envelope, que muda a cada gravação por causa do nonce aleatório), para o change
  tracking do EF não gerar `UPDATE` à toa. **Não há recifra das linhas existentes**: a migration só alarga a coluna, não
  decifra/recifra o que já estava em texto puro — uma linha gravada antes desta mudança fica ilegível (o `Unprotect` lança) até
  o documento passar de novo pelo pipeline (`reprocess` ou `reclassify-and-extract`, que regravam os campos). Decisão
  deliberada: PoC com dado sintético/mascarado, sem dataset real em produção para migrar.
- Testes: ver a contagem no fim da seção de autenticação abaixo (auditoria + OIDC/RBAC + cifra de campos juntos).

**Autenticação (SSO na API).** JWT bearer na API, ainda sem o login do BFF nem o Keycloak de desenvolvimento (trabalho
paralelo de outro agente); decisão e alternativas em `docs/adr/0003-oidc-jwt-bearer-na-api.md`:

- `OIDC_AUTHORITY` vazio (padrão) mantém o modo anônimo exatamente como sempre foi: nenhum esquema JWT é registrado,
  `AnonymousAccessGateMiddleware` continua gatendo `/api` por `ALLOW_ANONYMOUS_ACCESS`. `OIDC_AUTHORITY` preenchido
  registra `AddJwtBearer` (nunca `AddOpenIdConnect`: a API é resource server puro, quem faz login é o BFF) e faz o
  gate anônimo sair da frente por completo — os dois modos nunca se sobrepõem. `OIDC_CLIENT_ID` (dobra como audiência
  esperada do token), `OIDC_ADMIN_ROLE` (padrão `docreader-admin`) e `OIDC_USER_ROLE` (padrão `docreader-user`, ainda
  não exigido em endpoint nenhum) completam `DocReader:Security:Oidc` (`OidcOptions`/`OidcOptionsValidator` em
  `apps/api/Options`).
- Com OIDC ligado, uma `FallbackPolicy` (`RequireAuthenticatedUser`) passa a exigir token válido em todo controller;
  `AdminBackupController`, `AdminRestoreController` e `AdminStorageMigrationController` (os três `/api/v1/admin/*`)
  ganharam além disso `[Authorize(Policy = AuthorizationPolicies.AdminOnly)]`, que exige o papel `OIDC_ADMIN_ROLE`.
  Sem OIDC, essa mesma policy é uma assertiva sempre-verdadeira: os três controllers continuam funcionando sem token,
  provado por teste. Nenhum outro controller ganhou `[Authorize(Roles=...)]` — RBAC granular neles é trabalho futuro
  (o `TODO(RBAC)` de `StorageRepositoriesController.GetConnectionConfigAsync` continua de pé).
- Keycloak aninha papéis em `realm_access.roles`, que o tratamento padrão de JWT do ASP.NET Core não entende como
  papel: `KeycloakRoleClaims.ExpandRealmRoles` achata isso em claims `ClaimTypes.Role`, aplicado pela
  `KeycloakRealmRolesClaimsTransformation` (`IClaimsTransformation`, só registrada com OIDC ligado).
- `GET /api/v1/me` devolve `{ userId, email, name, roles }` do principal autenticado; em modo anônimo devolve `200`
  com payload fixo vazio em vez de `401` (não há token para ler).
- 401 sem token e 403 sem o papel exigido respondem `application/problem+json` (`UNAUTHENTICATED`/`FORBIDDEN`), não o
  corpo texto padrão do framework — `JwtBearerEvents.OnChallenge`/`OnForbidden` via `AuthProblemWriter`, mesmo
  formato do `ApiExceptionHandler`.
- Swagger ganha o botão "Authorize" (OAuth2 authorization code + PKCE) só quando `OIDC_AUTHORITY` está configurada;
  as URLs seguem a convenção de caminho do Keycloak, não uma descoberta real (ver limites no ADR 0003).
- Testes cobrem a policy `AdminOnly` nos dois modos (via `IAuthorizationService` real, sem `TestServer`), a
  validação de `OidcOptions`, o achatamento de `realm_access.roles` a partir de um JWT assinado de verdade
  (`System.IdentityModel.Tokens.Jwt`) e o mapeamento de `/me`; ficam em `tests/unit/DocReader.UnitTests/Api`, que
  agora referencia `apps/api` diretamente (sem `WebApplicationFactory`, que este repositório ainda não usa).
- Testes: contagem após juntar auditoria, OIDC/RBAC e cifra de campos — ver o número exato no resultado do `dotnet test`
  mais recente (as três seções somaram testes novos sobre a mesma base de 993).

**OIDC/SSO no web-bff, em progresso.** Lado do BFF (NextAuth.js) e um Keycloak de desenvolvimento prontos; a validação
de bearer token e `[Authorize(Roles=...)]` na API (`DocReader.Api`) são de um esforço paralelo, ainda não integrado
a este checkout.

- **Keycloak de desenvolvimento** (`docker-compose.dev.yml` apenas — nunca no compose de aceite): serviço `keycloak`
  (`quay.io/keycloak/keycloak:26.0`, `start-dev --import-realm`), realm `docreader` importado de
  `deploy/keycloak/docreader-realm.json` (cliente confidencial `docreader-bff` com PKCE, segredo de desenvolvimento
  fixo no arquivo — nunca um segredo real —, papéis de realm `docreader-admin`/`docreader-user`, usuários
  `admin@docreader.local`/`admin123` e `user@docreader.local`/`user123`, cada um com o papel correspondente, e um
  protocol mapper que garante `realm_access.roles` no ID token, no access token e no userinfo — é ali, não no
  caminho de papel padrão do ASP.NET Core, que o lado da API precisa ler o papel). Sobe com
  `docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d keycloak`, publicado em
  `KEYCLOAK_PORT` (padrão 8081). `KC_HOSTNAME` fixa o `iss` de todo token no endereço público
  (`http://localhost:8081/...`) não importa por qual URL a requisição chegou; `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`
  faz `token_endpoint`/`userinfo_endpoint`/`jwks_uri` responderem com o endereço de quem perguntou — confirmado
  contra o contêiner real: uma descoberta feita de dentro da rede do compose devolve `issuer` público e
  `token_endpoint` interno na mesma resposta. É o que permite o BFF chamar o token endpoint pela rede interna e
  ainda validar o token contra o emissor que o navegador viu.
- **NextAuth.js no BFF** (`next-auth@5.0.0-beta.32`, Auth.js — a única major com suporte real a App Router e
  peer dependency explícita para `next@^16`; `apps/web-bff/src/auth.ts`): provedor OIDC construído à mão em vez de
  `next-auth/providers/keycloak`, porque a descoberta automática (`/.well-known/openid-configuration`) usa um único
  `issuer` tanto para o host que o navegador acessa quanto para o host que o contêiner acessa — que aqui **são
  diferentes** (o problema clássico "Keycloak atrás do Docker"). `OIDC_AUTHORITY` (endereço interno,
  contêiner-a-contêiner, no padrão de `DOCREADER_API_BASE_URL`) constrói `token`/`userinfo`; `NEXT_PUBLIC_OIDC_AUTHORITY`
  (endereço público) constrói `authorization` (redirecionamento do navegador) **e** é o `issuer` passado ao provedor,
  porque é esse o valor que bate com o `iss` de todo token (ver acima) — o comentário em `auth.ts` explica por que a
  troca não pode ser feita ao contrário. `OIDC_AUTHORITY` vazio (padrão em todo lugar exceto o overlay de
  desenvolvimento) mantém o modo anônimo de sempre: `proxy.ts` (sucessor do `middleware.ts`, que o Next 16 já marca
  como obsoleto) não chama `auth()`, `layout.tsx` não lê sessão, `AnonymousAccessBanner` continua aparecendo. Com
  OIDC configurado, `proxy.ts` exige sessão em toda rota exceto `/login`, `/api/auth` e `/api/health`;
  `layout.tsx` (agora `async`, lê a sessão) mostra e-mail/papel e um botão "Sair"; `lib/api.ts#callApi` anexa
  `Authorization: Bearer <access_token>` em toda chamada à API quando há sessão — é esse token que o lado da API
  vai validar quando `[Authorize(Roles=...)]` existir. Fluxo completo (autorização, PKCE, login, callback, token
  exchange pela rede interna do Docker, sessão com papel) testado ponta a ponta por `curl` contra um contêiner real,
  para os dois usuários de teste; login interativo pelo navegador fica para conferência manual depois do merge.
- Variáveis novas em `.env.example`: `OIDC_AUTHORITY`, `NEXT_PUBLIC_OIDC_AUTHORITY`, `OIDC_CLIENT_ID`,
  `OIDC_CLIENT_SECRET`, `NEXTAUTH_SECRET` (gerar com `openssl rand -base64 32`), `KEYCLOAK_PORT` — todas
  vazias/comentadas por padrão. **Pendência de integração**: o esforço paralelo da API precisa validar o access
  token contra `realm_access.roles` (não o claim de papel padrão do ASP.NET) e concordar nos nomes acima; o jeito
  mais simples de reaproveitar o comportamento de `iss` fixo do Keycloak descrito acima é a API também apontar seu
  `Authority`/`MetadataAddress` para o endereço **interno** (mesma variável `OIDC_AUTHORITY`) e deixar o próprio
  documento de descoberta informar o emissor público — sem precisar de um `ValidIssuer` manual.
- **Integração confirmada**: o lado da API (acima) já lê `realm_access.roles` do jeito que o Keycloak deste overlay emite
  (`KeycloakRoleClaims.ExpandRealmRoles`), então os dois lados batem sem ajuste — falta só a verificação ao vivo pelo
  navegador (login interativo com os dois usuários de teste, `docreader-admin` chegando até `[Authorize(Policy=AdminOnly)]`).
- Testes: 1070 unitários (993 + 77 entre auditoria, OIDC/RBAC, cifra de campos e exclusão LGPD/GDPR juntos); 116 de
  integração, 110 passam e 6 são pulados de sempre (psql/pg_dump e Azurite indisponíveis), medido com PostgreSQL
  real na rede do compose depois de juntar as quatro seções.

**Fila alternativa: RabbitMQ (ADR 0004).** `QUEUE_PROVIDER` (`Postgres`, padrão, ou `RabbitMQ`) escolhe a
implementação de `IProcessingQueue`; **PostgreSQL continua o padrão e a ADR 0001 continua valendo** — RabbitMQ é
opt-in, ligado com `docker-compose.rabbitmq.yml` (`docker compose -f docker-compose.yml -f docker-compose.rabbitmq.yml
up --build`, que também sobe um `rabbitmq` de management com UI publicada). Nem `Domain` nem `Application` mudaram
uma linha para isto existir — a folga veio de `EnqueueAsync`/`CompleteAsync` já serem código morto desde a Etapa 3.

- **Outbox transacional** (`outbox_messages`, `DocReader.Infrastructure.Queue.OutboxMessage`): gravado na mesma
  `SaveChangesAsync` que a linha de `processing_jobs`, nos dois pontos reais de enfileiramento —
  `DocumentRepository.AcceptAsync` (upload) e `QueueNewAttemptAsync` (reprocessar/reclassificar) — só quando o
  provedor é RabbitMQ. **A API grava outbox, não só o worker**: upload e reprocessamento rodam no processo da API,
  então `QUEUE_PROVIDER` precisa estar setado ali também (sem as variáveis `RABBITMQ_*` de conexão — a API nunca
  abre conexão com o broker, só decide se grava a linha). Isso só apareceu numa verificação ao vivo: a primeira
  versão do overlay setava a variável só no `worker`, e um upload de teste ficou preso em `QUEUED`/`PENDING` para
  sempre com `outbox_messages` vazia. `RabbitMqOutboxPublisher` (`apps/worker`, molde de `WebhookDispatcher`) drena
  o outbox com `FOR UPDATE SKIP LOCKED`; com o broker fora do ar a publicação falha, loga e tenta de novo no próximo
  poll sem nunca desistir — confirmado ao vivo (upload com `rabbitmq` parado, documento ficou `QUEUED` com a linha
  do outbox sem `published_at`; `docker compose start rabbitmq` e o documento chegou a `COMPLETED` sozinho).
- **Ponte de DI** (`RabbitMqJobBridge`): `IProcessingQueue` é `Scoped` e o `ProcessingWorker` abre um escopo por
  job, então a conexão/canal/consumidor do RabbitMQ vivem num singleton à parte, registrado em
  `AddDocReaderInfrastructure` mas só promovido a `IHostedService` no `apps/worker/Program.cs`
  (`AddDocReaderRabbitMqConsumer()`) — a API nunca abre conexão, mesmo sabendo o provedor. Prefetch 1 (padrão),
  entregas viajam por um `Channel<T>` limitado até a fila escopada; `deliveryTag` fica num campo de instância da
  própria fila escopada (não um dicionário singleton), porque `AcquireNextAsync` e o desfecho do job sempre rodam
  na mesma instância dentro do mesmo escopo.
- **Idempotência**: `AcquireNextAsync` faz um `UPDATE ... WHERE id = @jobId AND status = 'PENDING'` mirado (não o
  `SKIP LOCKED LIMIT 1` do PostgreSQL, que é para escolher entre candidatos); zero linhas afetadas = duplicata ou
  job já resolvido por outra tentativa, confirmado (`ack`) sem nunca virar job novo.
- **Retry por fila de atraso por tentativa, não TTL por mensagem**: TTL por mensagem não funciona porque o RabbitMQ
  só expira da cabeça da fila. Uma fila `docreader.processing.jobs.retry.N` por tentativa que ainda pode repetir
  (`MaxAttempts - 1` filas), `x-message-ttl` calculado pela mesma `RetryBackoff.For` do PostgreSQL (não um valor
  fixo), `x-dead-letter-exchange`/`x-dead-letter-routing-key` de volta à fila principal pela exchange padrão.
- **Heartbeat continua no PostgreSQL** (`ProcessingJobBookkeeping`, compartilhado pelos dois provedores): a perda de
  conexão TCP do RabbitMQ só cobre worker morto, não um worker vivo e travado. `x-consumer-timeout` da fila
  principal (`ProcessingTimeout` + `RabbitMqOptions.ConsumerTimeoutMargin`, hoje 65 min) é a rede de segurança do
  broker para esse caso; a varredura de jobs presos do PostgreSQL mantém o mesmo sinal diagnosticável dos dois
  modos, mas sozinha não reatribui trabalho em modo RabbitMQ (só o broker redistribui uma entrega de verdade).
- ADR completa (mecanismo, alternativas consideradas, trade-offs) em `docs/adr/0004-rabbitmq-fila-alternativa.md`.
- Testes: 1086 unitários (1070 + 16 de seleção de provedor e cálculo de TTL, sem broker); 122 de integração, 116
  passam (110 + 6 novos contra RabbitMQ real) e 6 continuam pulados (psql/pg_dump e Azurite indisponíveis).

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
/schemas/documents   JSON Schemas por tipo documental (campos, sinais de classificação, validações)
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
docker-compose.rabbitmq.yml override que troca a fila para RabbitMQ (ADR 0004), opt-in
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

### Testes e avaliação

```bash
bash scripts/dotnet.sh test tests/unit/DocReader.UnitTests          # unitários; integração e demais suítes: README
docker run --rm -v "$PWD:/w" -w /w python:3.12-slim sh -c "pip install -q pytest && python -m pytest tests/accuracy -q"
python scripts/evaluate.py --dataset <pasta fora do repo> --api http://localhost:8080   # docs/evaluation.md
```

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

## Armadilhas conhecidas

Coisas que já custaram tempo e não se enxergam no código.

- **Amostra sintética aprova o que documento real reprova.** As regras da Etapa 3 exigiam frases inteiras que só as
  amostras traziam e deram `UNKNOWN` em CNH e RG reais. Ao mexer em classificação, rode o diagnóstico em um documento
  real, não só os testes. A **extração** ainda não foi calibrada assim: nos dois documentos reais de exemplo a CNH
  perde `name`, `registrationNumber`, `category` e `firstLicenseDate`, e o RIC perde quase tudo.
- **Cobertura alta em amostra não é cobertura em documento.** Dois exemplares (uma CNH e um RIC) calibraram esta etapa; outro
  estado, outra geração de layout ou foto ruim vão trazer rótulos e ordens novas. O primeiro passo é sempre
  `extraction-diagnostics` no documento que falhou, e o segundo é uma fixture mascarada em `Fixtures/ocr`.
- **Valor errado com status VALID é pior que NOT_FOUND.** O `birthPlace` do RIC saía `VALID` com o número vertical da borda
  da carteira, que o OCR entrega como bloco alto e estreito alinhado ao rótulo. Confira o valor, não só o status.
- **Resultado gravado não muda sozinho.** Depois de mudar regras, `recorded` e `current` do diagnóstico divergem até o
  `POST .../reprocess`; o `/result` continua mostrando a classificação antiga.

- **Disco cheio derruba o Docker.** O disco virtual do Docker Desktop (`docker_data.vhdx`) fica somente leitura
  quando o `C:` chega a zero, e sem Docker não há build .NET (`scripts/dotnet.sh` também roda em contêiner).
  Nunca apague o `.vhdx` nem resete o Docker Desktop: os volumes `docreader_postgres-data` e
  `docreader_document-storage` vivem nele. Limpe só o descartável (`docker builder prune`, os volumes
  `ocr-bench-models` e `ocr-onednn-venvs`) e **nunca** use `docker system prune --volumes`.
- **No Windows, use o helper PowerShell.** O Bash do WSL não enxerga o Docker: `powershell -File
  scripts/dotnet.ps1 test tests/unit/DocReader.UnitTests`. `dotnet ef` usa `src/DocReader.Infrastructure` como
  projeto de inicialização (o da API não referencia o pacote Design).
- **Chave Guid atribuída pelo domínio precisa de `ValueGeneratedNever`.** Sem isso o EF trata um filho novo de
  uma entidade rastreada como linha existente e emite `UPDATE` (0 linhas afetadas) em vez de `INSERT`.
- **`AcquireNextAsync` não abre transação explícita**: com `EnableRetryOnFailure` (produção) isso lança. Toda
  transação passa pela estratégia de execução; o fixture de integração usa retry justamente para isso não voltar.
- **A latência do OCR depende do limite de CPU do contêiner** (15,8 s contra 28 s numa página densa, ADR 0002).
  Não imponha `cpus:` ao `ocr-service` sem reler o ADR.
- **Heredoc com interpolação C# (`$"..."`) quebra o Bash tool**: escreva o arquivo com a ferramenta de escrita
  ou rode `node arquivo.js`.
- **Integração precisa de PostgreSQL alcançável.** Sem banco os 84 testes são pulados e o resumo diz "Zero tests ran". Rode dentro da rede do compose (`docker run --network docreader_internal ... -e DOCREADER_TEST_CONNECTION=...`, comando no README).
- **Toda migration nova exige `-c Release` limpo.** O Docker compila com warnings-as-errors e doc XML: um `<param>` faltando (CS1573) só aparece no build de Release.
- **A chave de cifra não pode mudar** depois de haver repositórios de armazenamento com configuração: o AES-GCM não decifra com outra chave, e a configuração cifrada se perde. O mesmo vale para `EXTRACTED_FIELD_ENCRYPTION_KEY` e os campos extraídos já gravados.
- **O EF Core cacheia o modelo compilado por tipo de `DbContext`, não por instância**: o `ValueConverter` de `RawValue`/`NormalizedValue` fecha sobre o `IFieldEncryptionProtector` recebido no construtor, mas só o da primeira instância de `DocReaderDbContext` construída no processo entra no modelo — construir outra instância com um protetor diferente não reconstrói o modelo nem troca esse fechamento. Em produção isso não importa (uma chave, o processo inteiro); em teste, todo `DocReaderDbContext` construído à mão precisa usar a mesma chave (`TestFieldEncryptionKey` em `Fakes/StorageFakes.cs` nos unitários, `PostgresFixture.FieldEncryptionProtector` na integração), senão o teste que perder a corrida para construir o modelo primeiro decifra com a chave errada.
- **Fixtures de OCR são texto real, não fabricado.** Depois de mudar engine, pré-processamento ou amostras,
  recapture com `scripts/capture-ocr-fixtures.py` antes de mexer nos extratores.
