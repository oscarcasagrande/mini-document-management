# ADR 0002 — Engine de OCR: PaddleOCR PP-OCRv5 mobile, em CPU, atrás de `IDocumentOcrProvider`

- **Status:** Aceita, com uma pendência obrigatória de latência (ver "Pendência")
- **Data:** 2026-09-25
- **Contexto do plano:** Etapa 2 — OCR ponta a ponta
- **Relacionada a:** PRD §10, RF-003, RF-009, RNF de latência (§ critérios de aceite), ADR 0001
- **Evidência:** `docs/bench/README.md` e os JSONs `ocr-benchmark-v5-mobile.json`,
  `ocr-onednn-pp3.2.2-mkldnn-false.json`, `ocr-onednn-pp3.2.2-mkldnn-true.json`;
  `scripts/ocr_benchmark.py`, `scripts/ocr_onednn_experiment.sh`

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
6. **Fora da Etapa 2:** PP-StructureV3, PDF com camada de texto nativa (RF-009), orientação e deskew.

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

## Pendência obrigatória

**Páginas A4 não atendem o requisito de 5 páginas em 90 s** (~18 s por página). Mesmo com oneDNN, as
duas páginas A4 medidas levaram 22,7 s e 31,0 s (cinco páginas: 113 a 155 s); sem oneDNN, 26 a 47 s por
página. Cartões (5 a 9 s) estão bem dentro do necessário.

Isto precisa ser resolvido **antes do contrato social (Etapa 3)**, que é multipágina. Hipóteses a medir,
sem ordem de prioridade estabelecida: PP-OCRv6 (declarado no script de benchmark, nunca medido), redução
de resolução antes do OCR e mais núcleos para o contêiner. Nenhuma foi medida; não há promessa de que
alguma delas feche a diferença.

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
- A imagem do `ocr-service` carrega os modelos: o build é lento e grande, mas a subida é previsível.
- PDF com camada de texto nativa (RF-009) segue sem tratamento: todo PDF passa por raster e OCR.

## Gatilhos de revisão

- Uma `paddlepaddle` 3.3.x com oneDNN funcionando em CPU.
- Medição de PP-OCRv6 ou de redução de resolução que feche a pendência de latência.
- Documento de tipo com tabela que o PP-OCRv5 puro não entregue (Etapa 3).
