# Benchmark de OCR — Etapa 2

Medição que sustenta a escolha de engine da Etapa 2. O objetivo é responder uma pergunta só:
**quanto custa a mais a pipeline de layout e o que ela entrega em troca**, em CPU, com as amostras
sintéticas do projeto.

## Como rodar

```bash
# 1. amostras com texto de verdade (as da Etapa 1 não servem: não têm texto)
docker run --rm -v "$PWD:/w" -w /w python:3.12-slim bash -c \
  "apt-get update -qq && apt-get install -y -qq fonts-dejavu-core >/dev/null \
   && pip install -q pillow && python scripts/make-ocr-samples.py"

# 2. imagem de medição (descartável, ~2,3 GB)
docker build -f deploy/docker/Dockerfile.ocr-bench -t docreader/ocr-bench .

# 3. medição
docker run --rm --cpus=4 --memory=6g \
  -v "$PWD/scripts:/scripts:ro" \
  -v "$PWD/samples/synthetic/ocr:/samples:ro" \
  -v ocr-bench-models:/var/lib/docreader/ocr-models \
  -v "$PWD/docs/bench:/out" \
  docreader/ocr-bench --repeat 2
```

Saída em `docs/bench/ocr-benchmark-<pipeline>.json`. Para medir uma pipeline só, use
`--only "PP-OCRv5 (det mobile)"` com `--output` próprio.

## Método

- **Amostras**: cartão de CPF (limpo e digitalizado) e páginas A4 a 200 DPI com texto denso e com
  tabela, cada uma também em versão digitalizada — rotação leve, ruído e borrão. São geradas por
  `scripts/make-ocr-samples.py`, então o conjunto é reprodutível e não contém documento real.
- **Aquecimento descartado**: a primeira passada de cada amostra paga caches internos e alocação de
  buffers; ela roda e o tempo é jogado fora.
- **Regime**: `--repeat` medições por amostra, reportando mediana e p95.
- **Normalização por área**: `ms/MP` permite comparar um cartão de 0,8 MP com uma página A4 de
  3,9 MP sem que o tamanho domine a leitura.
- **Contrapartida medida junto**: blocos de texto, caracteres e tabelas reconhecidas. Sem essa
  coluna a comparação seria enviesada — medir só latência faria a pipeline mais simples vencer por
  definição, já que o custo extra da pipeline de layout existe justamente para produzir estrutura.
- **Limites do contêiner**: `--cpus=4 --memory=6g`, para que o número não dependa da máquina inteira.

## Achados de operação

Os problemas concretos que apareceram na montagem e valem ficar registrados, porque afetam qualquer
imagem de OCR que este projeto venha a publicar.

### oneDNN precisa ficar desligado nesta build

Com o backend oneDNN ativo, **toda** inferência falha nesta build de CPU do `paddlepaddle 3.3.1`:

```
(Unimplemented) ConvertPirAttribute2RuntimeAttribute not support
[pir::ArrayAttribute<pir::DoubleAttribute>]
(at paddle/fluid/framework/new_executor/instruction/onednn/onednn_instruction.cc:116)
```

A variável de ambiente `FLAGS_use_mkldnn=false` resolve e tem precedência sobre o parâmetro
`enable_mkldnn` da pipeline — com a variável em `true`, passar `enable_mkldnn=False` não adianta.
Desligar o PIR (`FLAGS_enable_pir_api=0`) não destrava.

Isso tem custo: oneDNN é justamente a biblioteca de aceleração em CPU. Os números da rodada final
acima são, portanto, o **pior caso**. O experimento da seção seguinte mostrou que a `paddlepaddle`
**3.2.2** roda com oneDNN; é a versão adotada no `ocr-service` (ADR 0002), com `OCR_ENABLE_MKLDNN=false`
para reverter.

### `paddlex[ocr]` é obrigatório para o PP-StructureV3

Sem o extra, a pipeline falha na **criação**, não na inferência:

```
`PP-StructureV3` requires additional dependencies.
To install them, run `pip install "paddlex[ocr]==<PADDLEX_VERSION>"`
```

### O detector padrão de `lang="pt"` é o server, e ele domina a latência

`PaddleOCR(lang="pt", ocr_version="PP-OCRv5")` escolhe `PP-OCRv5_server_det` para detecção e
`latin_PP-OCRv5_mobile_rec` para reconhecimento. Trocar a detecção para `PP-OCRv5_mobile_det` deu,
no cartão de CPF, **7,9 s contra 20,9 s** — com os mesmos 12 blocos lidos. Por isso a medição
compara as duas variantes, e não só "v5 contra StructureV3".

Forçar `text_det_limit_side_len=960` piorou (9,9 s): o redimensionamento não compensa em imagem
pequena.

### O padrão do `paddleocr` 3.7 já não é o PP-OCRv5

Sem `ocr_version` explícito, a versão 3.7.0 baixa `PP-OCRv6_medium_det` e `PP-OCRv6_medium_rec`. O
benchmark fixa a versão em cada configuração. O PP-OCRv6 está declarado como candidato no script mas
**não foi medido**: a memória da máquina não permitiu completar as quatro configurações, e a
prioridade foi a comparação que o pedido nomeou.

## Resultados

Medido em contêiner com `--cpus=4 --memory=6g`, host de 4 núcleos físicos, stack de aplicação
parada, oneDNN desligado (ver achados acima). `--repeat 2`, aquecimento descartado.

### PP-OCRv5 — detecção mobile, reconhecimento `latin_PP-OCRv5_mobile_rec`

Rodada final (`ocr-benchmark-v5-mobile.json`, `paddlepaddle` 3.3.1, oneDNN desligado). Carga de modelo
**6,2 s**. Pico de RSS **2,09 GB**. Completou as 6 amostras. A coluna "Máx" é a maior das duas
medições (com `--repeat 2` não há p95 que se sustente).

| Amostra | MP | Mediana | Máx | ms/MP | Blocos | Chars | Tabelas |
|---|---:|---:|---:|---:|---:|---:|---:|
| `cpf-card-limpo.png` | 0,77 | 8,6 s | 8,9 s | 11.199 | 12 | 252 | 0 |
| `cpf-card-escaneado.png` | 0,81 | 6,9 s | 7,2 s | 8.425 | 12 | 252 | 0 |
| `pagina-tabela.png` | 3,87 | 27,8 s | 28,3 s | 7.184 | 62 | 667 | 0 |
| `pagina-texto-densa.png` | 3,87 | 40,9 s | 41,0 s | 10.573 | 36 | 2.093 | 0 |
| `pagina-tabela-escaneada.png` | 3,96 | 26,1 s | 26,1 s | 6.579 | 63 | 668 | 0 |
| `pagina-texto-densa-escaneada.png` | 3,99 | 39,0 s | 39,2 s | 9.766 | 33 | 2.089 | 0 |

Total das 6 amostras: 149,2 s para 17,27 MP, **8.638 ms/MP ponderado**. CPF, nome e nascimento dos
dois cartões, e os totais e CNPJ das páginas, saíram exatos (campo `expected` do JSON).

Medida à parte, a mesma amostra varia entre execuções: na primeira rodada, o cartão limpo deu 8,0 s e
11,3 s em execuções diferentes (~40%). Números desta tabela servem para ordem de grandeza, não para
comparar 10%.

### Com oneDNN (`paddlepaddle` 3.2.2)

Experimento de `scripts/ocr_onednn_experiment.sh`; a 3.3.1 falha com oneDNN (achado abaixo), a 3.2.2 roda
e devolve texto e confiança idênticos. Controle = 3.2.2 com oneDNN desligado.

| Amostra | Sem oneDNN | Com oneDNN | Ganho |
|---|---:|---:|---:|
| `cpf-card-limpo.png` | 8,5 s | 5,6 s | 34% |
| `cpf-card-escaneado.png` | 6,9 s | 4,9 s | 29% |
| `pagina-tabela.png` | 29,5 s | 22,7 s | 23% |
| `pagina-texto-densa.png` | 47,2 s | 31,0 s | 34% |

Pico de RSS 2,07 → 2,28 GB. Uma rodada exploratória anterior deu 18,1 s na página com tabela; a
tabela acima usa a rodada registrada em `ocr-onednn-pp3.2.2-mkldnn-true.json`.

### PP-OCRv5 — detecção server (padrão de `lang="pt"`)

Medido só no cartão, o suficiente para descartar: **18,4–22,6 s** contra 7,2–11,3 s da variante
mobile, com **os mesmos 12 blocos e 252 caracteres**. O detector server custa ~2,6× e não entrega
nada a mais neste conjunto.

### PP-StructureV3 — não completou

Carga de modelo **106,8 s** a frio (9+ modelos: orientação de documento e de linha,
`PP-OCRv5_server_det`, `PP-OCRv5_server_rec`, classificação de tabela, SLANeXt_wired, SLANet_plus e
dois detectores de célula RT-DETR-L), 11,7–18,3 s com cache quente.

| Amostra | MP | Resultado |
|---|---:|---|
| `cpf-card-escaneado.png` | 0,81 | **28,2 s** — 8 blocos, 251 chars, 0 tabelas |
| `cpf-card-limpo.png` | 0,77 | **28,6 s** — 7 blocos, 252 chars, 0 tabelas |
| `tabela-110dpi.png` (reduzida) | 1,17 | **OOM** — `exit=137`, `OOMKilled=true` |
| `pagina-tabela.png` | 3,87 | **OOM** — `exit=137`, `OOMKilled=true` |
| `pagina-texto-densa.png` | 3,87 | **OOM** — `exit=137`, `OOMKilled=true` |

Nos cartões leu os mesmos ~251 caracteres que o PP-OCRv5, em menos blocos — ele agrupa linhas em
blocos de layout, não lê menos. Custou **2,5–3,9× mais tempo** para o mesmo texto.

**Toda página de 1,17 MP ou mais foi morta por falta de memória**, com ou sem tabela, num limite de
6 GiB. A VM do Docker nesta máquina tem 8 GB no total, então não havia folga para tentar mais alto
sem inanir o host.

**Limitação desta medição, dita com todas as letras:** a capacidade que justificaria o custo do
PP-StructureV3 — reconhecimento de tabela — **nunca chegou a ser demonstrada**, porque as páginas que
a exercitariam não couberam na memória. O que se sabe é o custo, não o benefício.

## Seguimento de 2026-09-25: latência em A4, PP-OCRv6 e redução de resolução

Medição que fechou a pendência de latência do ADR 0002. Os números, a leitura e a decisão estão no ADR;
aqui ficam o método e os arquivos.

```bash
# matriz completa: v5 mobile, v6 medium, small e tiny, com e sem redução para 2000 px, com 4 CPUs
docker run --rm --user root --cpus=4 --memory=3g --entrypoint bash \
  -v "$PWD/scripts:/scripts:ro" -v "$PWD/samples/synthetic/ocr:/samples:ro" \
  -v ocr-bench-models:/models -v "$PWD/docs/bench:/out" \
  docreader/ocr-service:stage2 /scripts/ocr_latency_experiment.sh

# o padrão sem limite de CPU (o contêiner vê todas as threads da máquina)
docker run --rm --user root --memory=3g --entrypoint bash -e OMP_NUM_THREADS=4 -e OUT_SUFFIX=-cpus8-omp4 \
  -v "$PWD/scripts:/scripts:ro" -v "$PWD/samples/synthetic/ocr:/samples:ro" \
  -v ocr-bench-models:/models -v "$PWD/docs/bench:/out" \
  docreader/ocr-service:stage2 /scripts/ocr_latency_experiment.sh "PP-OCRv5 (det mobile)|0"

# recall por linha e por caractere contra o texto que o gerador desenhou
docker run --rm --entrypoint python -v "$PWD:/w" -w /w docreader/ocr-service:stage2 scripts/ocr_recall.py
```

Saída: `ocr-latency-<pipeline>-side<N>[<sufixo>].json`, um por configuração (`side0` é sem redução).

- **O que mudou no benchmark.** `--max-side N` reduz a imagem com o mesmo `_limit_side` do serviço e inclui
  decodificação e redução no tempo medido; as variantes `PP-OCRv6 medium|small|tiny` fixam detector e
  reconhecedor do mesmo tamanho.
- **O critério de valores-chave exatos não basta.** Todas as configurações o cumpriram, e o v6 small
  perdeu cinco ou seis linhas inteiras de um parágrafo na página limpa. `ocr_recall.py` procura no que a
  engine leu cada linha e cada célula que o gerador desenhou (similaridade ≥ 0,9, sem acento e sem caixa)
  e informa o recall por item e por caractere.
- **O regime de CPU pesa mais que o modelo.** O mesmo v5 mobile leva 15,8 s na página densa com todas as
  threads e 28,0 s limitado a 4 CPUs.
- **Exatidão na tarefa real.** `scripts/capture-engine-fixtures.ps1` captura o OCR das amostras da Etapa 3
  com cada perfil (`OCR_MODEL_PROFILE`), e a mesma bateria de extração roda sobre cada captura com
  `DOCREADER_OCR_FIXTURES=<pasta>`: 71/71 campos com v5 e v6 medium, 69/71 com small, 67/71 com tiny.
