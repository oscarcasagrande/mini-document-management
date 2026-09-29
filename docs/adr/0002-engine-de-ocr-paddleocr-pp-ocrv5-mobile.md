# ADR 0002 — Engine de OCR: PaddleOCR PP-OCRv5 mobile, em CPU, atrás de `IDocumentOcrProvider`

- **Status:** Aceita. A pendência de latência em A4 foi medida e resolvida em 2026-09-25 (ver "Latência em
  páginas A4: medição de seguimento"): o alvo é atingido sem limite de CPU e não é atingido com 4 CPUs
- **Data:** 2026-09-25 (decisão); seguimento de latência e de exatidão na Etapa 3 em 2026-09-25; adendo do
  RF-009 (texto nativo de PDF, orientação, PP-StructureV3 sob demanda) em 2026-09-29
- **Contexto do plano:** Etapa 2 — OCR ponta a ponta
- **Relacionada a:** PRD §10, RF-003, RF-009, RNF de latência (§ critérios de aceite), ADR 0001
- **Evidência:** `docs/bench/README.md` e os JSONs `ocr-benchmark-v5-mobile.json`,
  `ocr-onednn-pp3.2.2-mkldnn-false.json`, `ocr-onednn-pp3.2.2-mkldnn-true.json` e `ocr-latency-*.json`;
  `scripts/ocr_benchmark.py`, `scripts/ocr_onednn_experiment.sh`, `scripts/ocr_latency_experiment.sh`,
  `scripts/ocr_recall.py`, `scripts/capture-engine-fixtures.ps1`

## Contexto

A Etapa 2 liga o OCR de ponta a ponta. A PoC roda só em CPU, sem cloud e sem API proprietária, então a
engine precisa caber em um contêiner com memória limitada e reconhecer português com acentuação. As
amostras são sintéticas e reprodutíveis (`scripts/make-ocr-samples.py`): cartão de CPF limpo e
digitalizado, páginas A4 a 200 DPI com texto denso e com tabela, cada uma também digitalizada.

A pergunta medida foi: quanto custa a mais uma pipeline de layout (PP-StructureV3) e o que ela entrega
em troca do PP-OCRv5 puro.

## Decisão

1. **Engine:** PaddleOCR 3.7.0 com **PP-OCRv5**, detecção `PP-OCRv5_mobile_det` e reconhecimento
   `latin_PP-OCRv5_mobile_rec`. Versões fixadas em `services/ocr-service/requirements.txt`
   (`paddlepaddle` 3.2.2, `paddlex[ocr-core]` 3.7.2, `paddleocr` 3.7.0). Sem `ocr_version` explícito o
   `paddleocr` 3.7 baixaria PP-OCRv6; a versão é sempre fixada.
2. **Fronteira:** a API e o worker só conhecem `IDocumentOcrProvider`. `PaddleOcrServiceProvider`
   fala HTTP com o `ocr-service` (`POST /v1/ocr/page`, uma página por chamada), então trocar a engine
   não toca domínio nem API pública.
3. **Uma página por chamada, concorrência 1.** O `ocr-service` serializa a inferência
   (`InferenceLimiter`), com timeout por página; o slot só é liberado quando a thread termina, para um
   timeout não empilhar inferências sobre a memória. O contêiner tem `mem_limit` de 3 GiB.
4. **Modelos baixados e aquecidos no build da imagem**, para o `docker compose up` não depender de rede
   nem pagar carga a frio na primeira requisição.
5. **oneDNN ligado**, com `paddlepaddle` 3.2.2. `OCR_ENABLE_MKLDNN=false` reverte.
6. **Perfil de modelo selecionável**, `OCR_MODEL_PROFILE`: `ppocrv5-mobile` (padrão), `ppocrv6-medium`,
   `ppocrv6-small` e `ppocrv6-tiny`. Só os modelos do padrão vão na imagem; trocar o perfil exige
   baixar os do outro (ou uma imagem construída com ele). Um nome desconhecido derruba o serviço na
   subida, não na primeira página.
7. **Fora da Etapa 2:** PP-StructureV3, PDF com camada de texto nativa (RF-009), orientação e deskew.

## Números

Contêiner com `--cpus=4 --memory=6g`, `--repeat 2`, aquecimento descartado, mediana.

| Medida | Valor |
|---|---|
| Cartão de CPF, oneDNN desligado, 3.3.1 | 6,9 s (escaneado) e 8,6 s (limpo) |
| Página A4, oneDNN desligado, 3.3.1 | 26,1 a 40,9 s |
| Carga de modelo | 6,2 s |
| Pico de memória | 2,09 GB |
| Imagem de benchmark | 2,57 GB |
| Exatidão de CPF, nome e nascimento nos dois cartões | exata (campo `expected` do JSON) |

### Experimento oneDNN

A 3.3.1 falha toda inferência com oneDNN ligado (`ConvertPirAttribute2RuntimeAttribute not support`).
A 3.2.2 roda, com texto e confiança idênticos. Controle: 3.2.2 sem oneDNN contra 3.2.2 com oneDNN.

| Amostra | Sem | Com |
|---|---:|---:|
| Cartão limpo | 8,5 s | 5,6 s |
| Cartão escaneado | 6,9 s | 4,9 s |
| A4 com tabela | 29,5 s | 22,7 s (uma rodada exploratória deu 18,1 s) |
| A4 texto denso | 47,2 s | 31,0 s |

Pico de memória 2,07 → 2,28 GB. Adotado. A 3.2.2 é uma versão mais antiga que a 3.3.1: o ganho de 23 a
34% pagou o custo de ficar presa a ela, e a reversão é uma variável de ambiente.

### PP-StructureV3

Medido só em 2 cartões: ~28 s (2,5 a 3,9× o PP-OCRv5) para o mesmo texto. **OOM com 6 GiB nas três
páginas de 1,17 MP ou mais**, com ou sem tabela. **Reconhecimento de tabela nunca foi demonstrado**:
as páginas que o exercitariam não couberam na memória. Sabe-se o custo, não o benefício. Fica fora da
Etapa 2 e entra na Etapa 3 como opção configurável por tipo documental, medida com mais memória.

## Latência em páginas A4: medição de seguimento

A Etapa 2 terminou com uma pendência: páginas A4 levavam 23–47 s de OCR contra os ~18 s por página do
requisito (cinco páginas em 90 s, PRD §20). Antes da Etapa 3, o contrato social multipágina, três
hipóteses foram medidas: **PP-OCRv6**, **redução de resolução** para no máximo 2000 px no lado maior antes
do OCR e o **regime de CPU**. Tudo com oneDNN, `paddlepaddle` 3.2.2, as mesmas seis amostras, `--repeat 2`,
aquecimento descartado, tempo do redimensionamento incluído, e o serviço de aplicação parado durante a
medição. As reduções usam `app.pages._limit_side`, o mesmo código que a produção aplica.

O PP-OCRv6 tem três tamanhos; o padrão do `paddleocr` 3.7 é o `medium`. Os três foram medidos.

### Resultados

Tempo por página em segundos (mín.–máx. entre as amostras do grupo), pico de memória do processo, e o que
a engine deixou de ler. `Recall A4 denso` é a fração de caracteres do texto desenhado que a engine
reconheceu na página densa limpa e na digitalizada (`scripts/ocr_recall.py`). O critério original do
benchmark (valores-chave exatos) marcou todas as configurações como sem divergência, o que **não** pega
linhas perdidas, e por isso o recall foi acrescentado.

Contêiner limitado a **4 CPUs** e 3 GiB (o regime do benchmark original):

| Configuração | Cartão | A4 tabela | A4 texto denso | Pico RSS | Recall A4 denso |
|---|---:|---:|---:|---:|---|
| **v5 mobile (padrão)** | 4,7 | 15,5–17,6 | 25,5–28,0 | 2,30 GB | 100% / 100% |
| v5 mobile, máx. 2000 px | 4,2–5,4 | 15,0–17,0 | 24,4–28,1 | 1,94 GB | 100% / 100% |
| v6 medium | 7,3–8,2 | 27,3–28,4 | 39,5–40,3 | **3,29 GB** | 100% / 100% |
| v6 medium, máx. 2000 px | 6,8–8,0 | 23,1–24,1 | 36,5–37,8 | 2,75 GB | 96% / 100% |
| v6 small | 2,0–2,2 | 7,8 | 9,7–10,3 | 1,88 GB | **78%** / 100% |
| v6 small, máx. 2000 px | 2,1 | 6,9–7,0 | 9,6–10,0 | 1,49 GB | **82%** / 100% |
| v6 tiny | 0,6 | 2,7–3,0 | 3,5–3,7 | 1,52 GB | 97% / 100% |
| v6 tiny, máx. 2000 px | 0,7–0,9 | 2,3–3,1 | 3,1–3,2 | 1,25 GB | 99% / 100% |

**Sem limite de CPU** (o contêiner vê as 8 threads lógicas da máquina, como o compose faz hoje), só o padrão:

| Configuração | Cartão | A4 tabela | A4 texto denso | Pico RSS |
|---|---:|---:|---:|---:|
| v5 mobile, `OMP_NUM_THREADS=4` | 1,9–2,9 | 9,2 | 15,3–15,8 | 2,26 GB |
| v5 mobile, `OMP_NUM_THREADS=8` | 2,4–2,8 | 9,1–9,5 | 14,9–15,4 | 2,17 GB |

Exatidão da **extração** nas seis amostras da Etapa 3 (CIN, CNH, comprovante de residência, cartão CNPJ,
CCMEI, contrato social: 71 campos com valor e status esperados), com o OCR de cada perfil passando pelos
mesmos extratores (`DOCREADER_OCR_FIXTURES`, `scripts/capture-engine-fixtures.ps1`):

| Perfil | Campos corretos | O que errou |
|---|---:|---|
| v5 mobile | 71 / 71 | — |
| v6 medium | 71 / 71 | — |
| v6 small | 69 / 71 | CNPJ do cabeçalho do contrato não lido; nome de sócio lido "DASILVA" |
| v6 tiny | 67 / 71 | categoria e 1ª habilitação da CNH não lidas; CNPJ do contrato não lido; "100- CENTRO" no endereço do CCMEI |

### O que os números dizem

- **Redução de resolução não ajuda.** A página A4 do benchmark tem 3,9 MP; reduzi-la a 2,8 MP não mudou o
  tempo do v5 (28,0 → 28,1 s no texto denso; 15,5 → 17,0 s na tabela): o custo está no reconhecimento das
  linhas, não na detecção. Custou recall (caracteres da tabela: 97% → 94%). Só baixa o pico de memória.
- **O PP-OCRv6 medium é mais lento que o v5 mobile** (1,4 a 1,6×) e o pico de 3,29 GB passa do limite de
  3 GiB do compose: não cabe.
- **O v6 small (2 a 3× mais rápido) e o tiny (6 a 8×) são menos exatos.** O small perdeu 5 a 6 linhas inteiras
  de um parágrafo denso na página limpa (as de vigência, a de `R$ 4.780,00`, a Cláusula Quarta), apesar de
  ter lido a mesma página digitalizada por inteiro: a detecção é menos estável. Na Etapa 3 o tiny falhou
  em 4 de 71 campos, e uma linha perdida de contrato é exatamente o erro que ninguém vê.
- **O regime de CPU pesa mais que o modelo.** O mesmo v5 leva 15,8 s (8 threads) ou 28,0 s (4 CPUs) na
  página densa. O benchmark original media com 4 CPUs para não depender da máquina inteira, o que
  superestimou a latência para esta máquina. `OMP_NUM_THREADS` 4 ou 8 não muda nada.

### Decisão

1. **O PP-OCRv5 mobile continua sendo o padrão.** É o único que junta 71/71 campos, recall máximo e
   memória dentro do limite. A redução de resolução não é adotada (`OCR_MAX_IMAGE_SIDE` segue em 3200,
   só como limite de segurança) e o v6 medium é rejeitado.
2. **O alvo de ≤ 18 s por página é atingido na máquina de referência, sem limite de CPU:** pior caso
   15,8 s (página A4 densa a 200 DPI), cinco páginas densas em ~79 s, abaixo dos 90 s do PRD. O contrato
   social de três páginas da Etapa 3 levou 14,3 + 8,9 + 7,7 s pelo compose. Máquina de referência: a desta
   PoC, 8 threads lógicas (4 núcleos físicos), 8 GB para a VM do Docker.
3. **O alvo não é atingido com o contêiner limitado a 4 CPUs:** 25,5–28,0 s no texto denso, cinco páginas
   densas em 130–140 s. Quem rodar assim tem duas saídas, nenhuma de graça: mais CPU, ou
   `OCR_MODEL_PROFILE=ppocrv6-small` (ou `ppocrv6-tiny`), aceitando as perdas de exatidão da tabela acima.
   Nenhuma das duas foi adotada porque o conjunto de teste é de seis documentos sintéticos: não sustenta
   trocar exatidão por velocidade. **A Etapa 4** (avaliação, com dataset e ground truth) decide se o small
   ou o tiny são aceitáveis, e com que perda.
4. **O compose não limita CPU do `ocr-service`, e a decisão depende disso.** Se um limite for imposto, o
   requisito de latência deixa de valer para páginas densas.

A medição é sintética: as amostras são páginas geradas, o texto é limpo e o cartão tem pouca variação. A
latência real de um scan de celular, de um PDF de 60 páginas ou de uma tabela com bordas ruidosas pode ser
maior; o desvio entre rodadas da mesma configuração chegou a ~40%.

## Progresso e recuperação de job longo

Um documento de várias páginas ocupa o job por dezenas de segundos. O desenho:

- `locked_at` é o heartbeat: o worker o renova a cada página concluída.
- A recuperação de job preso mede o **silêncio** desde o último heartbeat (`JobLockTimeout`, 5 min), não
  a idade da reserva. `ProcessingTimeout` (60 min) limita o total.
- **Fencing por tentativa:** `Heartbeat`, `Complete`, `Fail` e `Release` só valem para a tentativa que
  ainda é dona do job (`attempt_count`). Um worker que perdeu a reserva não sobrescreve o que assumiu.
- O resultado é gravado numa única transação: extração, campos, estado do documento e fechamento do
  job. Não existe resultado parcial marcado como `COMPLETED`.
- Falha transitória devolve o documento a `QUEUED`, mantém o erro visível e agenda nova tentativa com
  backoff (3 tentativas). Índice único parcial `ux_processing_jobs_document_id_active` garante um job
  vivo por documento.

## Alternativas consideradas

- **PP-StructureV3 como padrão.** Rejeitada: 2,5 a 3,9× mais lenta para o mesmo texto nos cartões,
  OOM em toda página de 1,17 MP ou mais, benefício de tabela não demonstrado.
- **Detector server (padrão de `lang="pt"`).** Rejeitada: 18 a 23 s contra 7 a 11 s no cartão, com os
  mesmos 12 blocos e 252 caracteres.
- **`text_det_limit_side_len=960`.** Rejeitada: piorou (9,9 s) em imagem pequena.
- **PP-OCRv6.** Não medida. Continua como hipótese da pendência de latência.

## Consequências

- O texto reconhecido nas amostras está salvo em `docs/bench/ocr-benchmark-v5-mobile.json`, o que
  permite comparar uma futura engine sem repetir a medição da atual.
- O `ocr-service` fica preso à `paddlepaddle` 3.2.2 até a 3.3.x corrigir oneDNN em CPU.
- A latência de página é função do regime de CPU: o requisito de 90 s para cinco páginas vale para o
  contêiner sem limite de CPU na máquina de referência, não para um limite de 4 CPUs.
- A imagem do `ocr-service` carrega os modelos: o build é lento e grande, mas a subida é previsível.
- PDF com camada de texto nativa (RF-009) segue sem tratamento: todo PDF passa por raster e OCR.

## Adendo de 2026-09-29: RF-009 no `ocr-service`

O RF-009 tirou do "fora de escopo" três itens desta decisão: PDF com camada de texto nativa, orientação
e deskew, e PP-StructureV3 sob demanda. O PP-OCRv5 mobile continua sendo a engine; o que muda é o que
chega a ela e o que pode reler uma página depois dela. Medido na máquina de referência (8 threads, VM do
Docker com 7,7 GiB), `paddlepaddle` 3.2.2, `paddleocr` 3.7.0, `paddlex` 3.7.2, amostras sintéticas.
Script: `scripts/ocr_structure_memory.py`.

### PDF com camada de texto nativa

Uma página de PDF cujo texto embutido tem ao menos `OCR_PDF_NATIVE_TEXT_MIN_CHARS` (20) letras e dígitos é
lida pelo `pdfplumber` 0.11.10, sem rasterizar nem chamar o PaddleOCR: **0,1 s** por página (0,03 s com
o processo aquecido) contra 4 a 24 s de OCR. Os blocos vêm em coordenadas de pixel do mesmo espaço que a
rasterização a `OCR_PDF_DPI` produziria (a margem de 72 pt cai em x = 200 px a 200 DPI, verificado contra o
`pypdfium2`), confiança 1,0, uma linha partida onde há vão largo, como o detector faz. Vão para o OCR:
camada vazia ou só com número de página, camada de lixo (`(cid:N)`), página coberta por imagem com pouco
texto (digitalização com carimbo de assinatura digital) e página com `/Rotate` (o `pdfplumber` devolve
coordenadas giradas com o tamanho não girado). Marca d'água diagonal e nota de margem vertical ficam de fora
dos blocos. `provider` e `modelVersion` continuam os do PP-OCRv5, porque o .NET grava um por tentativa (o da
última página); quem leu a página está em `raw.source = "pdfplumber"` e em `hasNativeTextLayer`.

### Orientação e deskew

Sem modelo, em `app/preprocess.py`, antes do OCR, em toda página que vai ao OCR. O eixo das linhas sai da
variância do perfil de projeção (a melhor entre inclinações de até 20°, para uma página torta não parecer
deitada); o deskew, da mediana ponderada dos segmentos de Hough quase horizontais, e é aplicado entre 0,5° e
20°. A variância não distingue 0° de 180° (o perfil só se inverte), então o sentido vem de duas pistas:
ascendentes contra descendentes (texto em caixa mista; só votam as linhas que têm faixa de altura-x) e
margem esquerda alinhada contra direita irregular (que não se sobrepõe às letras, porque uma tabela com
números alinhados à direita a inverte). Na grade de avaliação (15 imagens × 4 rotações × 5 inclinações, 300
casos): **276 corretos, 224 de 224 inclinações endireitadas com resíduo < 0,5°, e nenhuma página em pé
girada**. Os 24 erros são abstenções: página a 180° deixada como chegou (cartão em maiúsculas centralizado,
tabela em maiúsculas com números à direita, tabela degradada, onde não há pista de sentido) ou página
deitada em empate, virada para 90° quando era 270°. Custo: 46 a 149 ms por página na imagem do serviço. Ponta a ponta: cartão de CPF girado 90° volta com `rotationDegrees = 90` e o CPF
lido; inclinado 10°, `deskewed = true` e confiança 0,999.

### PP-StructureV3 sob demanda: medições

1. **Não roda com o extra `ocr-core`.** Construir o pipeline falha ("A dependency error occurred during
   pipeline creation"). O extra `ocr` da mesma `paddlex` 3.7.2 resolve: +~260 MB de pacotes Python, nenhum
   peso de modelo (imagem 2,4 → 2,75 GB, com o `pdfplumber`).
2. **Não roda com oneDNN.** Com oneDNN ligado o processo corrompe o heap (`malloc(): unsorted double linked
   list corrupted`): segfault (139) numa execução, travado a 0% de CPU na outra. Com oneDNN desligado roda.
   O serviço mantém o PP-OCRv5 com oneDNN e a variável é lida uma vez por processo; por isso o
   PP-StructureV3 roda num **processo filho**.
3. **O OOM de 2026-09-25 vinha dos modelos de texto server, não das tabelas.** Com os padrões do
   PP-StructureV3 (detector e reconhecedor server), a `pagina-tabela.png` (1654 × 2339, **3,87 MP**; não
   2,1 MP) volta a estourar 6 GiB (137). Com o detector mobile e o reconhecedor latino mobile, os do
   serviço:

   | Página | MP | Limite | Pico de RSS do processo | Inferência | Resultado |
   |---|---:|---:|---:|---:|---|
   | pagina-tabela, reduzida | 1,25 | 6 GiB | 2,07 GB | 27–29 s | 1 tabela, 58 células |
   | pagina-tabela | 3,87 | 3 GiB | 2,84 GB | 34–40 s | 1 tabela, 57 células (todas) |
   | pagina-tabela | 3,87 | 6 GiB | 2,84 GB | 35–37 s | idem |
   | pagina-tabela-escaneada | 3,96 | 6 GiB | 2,91 GB | 32–36 s | 1 tabela, 58 células |
   | pagina-texto-densa | 3,87 | 6 GiB | 2,78 GB | 41 s | 0 tabelas |

   Carga do pipeline: 5 a 8 s, paga a cada chamada, porque o filho não fica residente.
4. **No serviço** (processo principal com o PP-OCRv5 + filho): pico do contêiner de **4,49 a 4,72 GB** com
   as três páginas de tabela. Com 6 GiB, nenhum OOM. Com os 3 GiB padrão, o kernel mata **o filho**
   (`oom_kill 1` no cgroup; o filho se oferece com `oom_score_adj = 1000`), a página volta com o resultado do
   PP-OCRv5 e status 200, e o contêiner não reinicia. A tabela degradada de 0,78 MP cabe em 3 GiB.
5. **Tempo por página de tabela:** 36 a 53 s ponta a ponta (PP-OCRv5 + filho), contra 4 a 12 s só com o
   PP-OCRv5.
6. **O gatilho não dispara nas tabelas limpas.** O PP-OCRv5 lê `pagina-tabela.png` e a escaneada com
   confiança média de 0,998 a 0,9995 na grade; com o limiar de 0,80 elas não vão ao PP-StructureV3. Para
   exercitar o caminho foi criada `pagina-tabela-degradada.png` (0,78 MP, grade a 0,64), que dispara.
7. **O benefício continua não demonstrado.** Das 57 células de valor conhecido, o texto exato encontrado
   caiu com o PP-StructureV3: 55 → 52 na limpa, 56 → 54 na escaneada, 3 → 0 na degradada (ambos falham
   nela). Ele entrega a estrutura (todas as células detectadas), não texto melhor.

### Decisão

1. `OCR_STRUCTURE_MAX_MEGAPIXELS` = **4,0**: cobre uma A4 a 200 DPI (3,87 MP), a maior página medida que
   cabe. Acima disso, aviso no log e a página fica com o PP-OCRv5. Não medido acima de 4 MP.
2. `OCR_MEMORY_LIMIT` padrão continua **3g**; quem liga `OCR_USE_PP_STRUCTUREV3_FOR_TABLES` precisa de
   **6g** (pico medido de 4,72 GB). O serviço loga um aviso na subida se o limite for menor. Não subir o
   padrão para todos por uma opção desligada.
3. O PP-StructureV3 roda em processo filho, com oneDNN desligado, `oom_score_adj = 1000` e morto no prazo
   da página. Qualquer falha (morto, travado, erro, modelos não baixados, outro filho rodando) mantém o
   resultado do PP-OCRv5 com um aviso.
4. Os pesos (~873 MB) não vão na imagem: com a opção ligada, são baixados em segundo plano na subida
   (95 s medidos) para o volume de cache, sem carregar os modelos. Até terminar, nenhuma página é
   enviada ao PP-StructureV3, e o `/health` informa `structureReady`.
5. **A opção continua desligada por padrão.** Nas amostras ela custa 25 a 40 s por página de tabela e não
   melhorou o texto. Quando dispara, substitui os blocos pelos do PP-StructureV3 (uma célula por bloco) e
   guarda as duas cargas em `raw` (`v5` e `structureV3`).

## Gatilhos de revisão

- Uma `paddlepaddle` 3.3.x com oneDNN funcionando em CPU.
- A avaliação da Etapa 4 mostrar que o `ppocrv6-small` ou o `ppocrv6-tiny` mantém a exatidão em dataset
  real, o que os tornaria o padrão em máquinas com menos CPU.
- Imposição de limite de CPU ao `ocr-service`.
- Documento de tipo com tabela que o PP-OCRv5 puro não entregue (Etapa 3). Em 2026-09-29 o PP-StructureV3
  passou a caber na memória (adendo acima) mas não leu melhor que o PP-OCRv5 nas amostras: o gatilho segue
  aberto até um documento real em que ele leia melhor. Se isso acontecer, rever também a substituição
  integral dos blocos (uma fusão por região de tabela pode ser melhor).
- PP-StructureV3 com oneDNN funcionando, o que tiraria a necessidade do processo filho.
