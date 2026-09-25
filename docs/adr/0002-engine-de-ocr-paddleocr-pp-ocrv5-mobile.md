# ADR 0002 — Engine de OCR: PaddleOCR PP-OCRv5 mobile, em CPU, atrás de `IDocumentOcrProvider`

- **Status:** Aceita. A pendência de latência em A4 foi medida e resolvida em 2026-09-25 (ver "Latência em
  páginas A4: medição de seguimento"): o alvo é atingido sem limite de CPU e não é atingido com 4 CPUs
- **Data:** 2026-09-25 (decisão); seguimento de latência e de exatidão na Etapa 3 em 2026-09-25
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

## Gatilhos de revisão

- Uma `paddlepaddle` 3.3.x com oneDNN funcionando em CPU.
- A avaliação da Etapa 4 mostrar que o `ppocrv6-small` ou o `ppocrv6-tiny` mantém a exatidão em dataset
  real, o que os tornaria o padrão em máquinas com menos CPU.
- Imposição de limite de CPU ao `ocr-service`.
- Documento de tipo com tabela que o PP-OCRv5 puro não entregue (Etapa 3).
