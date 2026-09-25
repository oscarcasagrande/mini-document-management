Amostras sintéticas. Nenhuma contém dado pessoal real: os CPFs e o CNPJ são exemplos de manual (dígito
verificador válido, origem didática), os nomes não existem e cada peça traz a marca AMOSTRA SINTETICA.

| Pasta | Gerador | Para quê |
|---|---|---|
| `./` (raiz) | `node scripts/make-samples.mjs` | Etapa 1: assinatura de arquivo, contagem de páginas, limites. Não têm texto |
| `ocr/` | `scripts/make-ocr-samples.py` | Etapa 2: benchmark de OCR (cartão de CPF, página densa, tabela; limpas e digitalizadas) |
| `documents/` | `scripts/make-stage3-samples.py` | Etapa 3: um documento de cada tipo estruturado, com `<amostra>.expected.json` |

## `documents/` (Etapa 3)

| Amostra | Tipo | Observação |
|---|---|---|
| `cin-frente-verso.png` | `BR_CIN` | Frente e verso na mesma imagem, com filiação e MRZ TD1 válida |
| `cnh.png` | `BR_CNH` | Rótulos numerados como na CNH (`4a DATA EMISSÃO`, `5 Nº REGISTRO`) |
| `comprovante-residencia.png` | `BR_PROOF_OF_ADDRESS` | Fatura de energia; CEP, cidade e UF numa linha só, sem rótulo |
| `cartao-cnpj.png` | `BR_CNPJ_CARD` | CNPJ **alfanumérico** (`12.ABC.345/01DE-35`); nome de fantasia `********` |
| `ccmei.png` | `BR_CCMEI` | CNPJ numérico legado; endereço em três linhas |
| `contrato-social.pdf` | `BR_SOCIAL_CONTRACT` | Três páginas, dois sócios, dados espalhados pelas páginas |

O `expected.json` de cada uma diz, por campo, o valor normalizado e o status esperados. É lido pelos testes
unitários (`Stage3SampleTests`, sobre o OCR real capturado em `tests/unit/DocReader.UnitTests/Fixtures/ocr`)
e pelo teste ponta a ponta (`tests/e2e/stage3_acceptance.py`).

```bash
# regenerar as amostras
docker run --rm -v "$PWD:/w" -w /w python:3.12-slim bash -c \
  "apt-get update -qq && apt-get install -y -qq fonts-dejavu-core >/dev/null \
   && pip install -q pillow && python scripts/make-stage3-samples.py"

# recapturar o OCR real (compose de pé)
docker run --rm --network docreader_internal -v "$PWD:/w" -w /w python:3.12-slim \
  python scripts/capture-ocr-fixtures.py --base-url http://ocr-service:8000
```
