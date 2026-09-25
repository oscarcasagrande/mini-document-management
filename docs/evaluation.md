# Avaliação

Como medir o DocReader contra documentos reais anotados: o formato do ground truth, as métricas, como rodar e como
ler o relatório. A ferramenta é `scripts/evaluate.py` (Python, só biblioteca padrão); os testes dela estão em
`tests/accuracy`.

O que existe hoje mede-se contra amostras **sintéticas**, que provam que o pipeline funciona e que as regras não
regridem, mas não dizem como o sistema se comporta em documento de verdade. Os indicadores do PRD §3 só têm
significado sobre um dataset real; este framework existe para produzi-lo.

## Regra de ouro: documento real fica fora do repositório

- O dataset (documentos e ground truth) mora numa pasta local **fora** deste repositório. O script recusa uma pasta
  dentro dele, exceto `samples/synthetic`; `--allow-inside-repo` contorna a recusa, e o `.gitignore` não é garantia.
- O `.gitignore` bloqueia imagens, PDFs, `*.truth.json` e as pastas `evaluation-report*/`, `dataset/` e
  `datasets/` na raiz, como rede de segurança.
- O relatório, por padrão, **não traz nome de arquivo nem valor de campo**: só números e, para cada documento com
  erro, um identificador anônimo (`doc-003-a1b2c3`) e o nome dos campos errados. `--details` identifica os
  documentos pelo caminho e `--include-values` grava o valor esperado e o devolvido: use os dois só para depurar, e
  trate o relatório como dado pessoal quando os usar.
- O script apaga da API os documentos que enviou, ao terminar cada um. `--keep` os preserva.
- Os documentos passam pelo sistema local (OCR local, banco e volume locais). Nada sai da máquina.

## Estrutura do dataset

```text
/dados/avaliacao/
  cnh-001.jpg
  cnh-001.truth.json          # ground truth do arquivo ao lado
  cin-002.png
  cin-002.truth.json
  contratos/
    contrato-003.pdf
    contrato-003.truth.json
```

O ground truth de `<arquivo>.<ext>` é `<arquivo>.truth.json`, na mesma pasta (o sufixo muda com `--truth-suffix`).
Subpastas são percorridas. Documento sem ground truth é ignorado e contado como "sem anotação"; ground truth sem
documento é contado como "anotação sem documento". Extensões: `.png .jpg .jpeg .pdf .tif .tiff`.

## Formato do ground truth

```json
{
  "documentType": "BR_CNH",
  "quality": "photo",
  "legible": true,
  "fields": {
    "name": "MARIA APARECIDA DA SILVA SOUZA",
    "cpf": "11144477735",
    "birthDate": "1985-03-14",
    "registrationNumber": { "status": "INVALID", "raw": "04512345678" },
    "category": { "value": "AB" },
    "expirationDate": { "value": "2023-07-01", "messages": ["DOCUMENT_EXPIRED"] },
    "firstLicenseDate": null
  }
}
```

| Chave | Obrigatória | Significado |
|---|---|---|
| `documentType` | sim | Tipo real do documento: um dos sete tipos com extrator (`BR_CPF_CARD`, `BR_CIN`, `BR_CNH`, `BR_PROOF_OF_ADDRESS`, `BR_CNPJ_CARD`, `BR_CCMEI`, `BR_SOCIAL_CONTRACT`) ou `UNKNOWN` para um documento de outro tipo, que o sistema deve devolver como `UNKNOWN` |
| `quality` | não | Rótulo livre para o relatório por qualidade: `clean`, `scan`, `photo`, `low-light`… (padrão `unspecified`) |
| `legible` | não | `false` tira o documento do indicador de texto utilizável (padrão `true`) |
| `fields` | não | O que o documento traz, por campo. O nome é o do schema em `schemas/documents/<tipo>.v1.json` |

Cada campo de `fields` tem uma de três formas:

| Forma | Significa |
|---|---|
| `"valor"` | O documento traz esse valor, **já normalizado como o sistema o normaliza** (CPF só dígitos, data ISO, CNPJ com 14 posições em caixa alta, nome em caixa alta sem acento, CEP com 8 dígitos…). Ver o exemplo de cada campo no schema |
| `null` | O documento **não traz** o campo. O sistema acerta devolvendo `NOT_FOUND`; devolver um valor é falso positivo |
| `{ … }` | Objeto com as chaves abaixo |

Chaves do objeto: `value` (o valor normalizado; `normalized` é aceito como sinônimo, é o nome nos `*.expected.json`
das amostras sintéticas), `status`, `raw` e `messages`.

| `status` | Uso |
|---|---|
| `VALID` | Padrão quando há `value`. O documento traz o valor e ele é correto |
| `INVALID` | O documento traz o valor **com defeito** (CPF com dígito errado, data impossível). Não tem `value`; `raw` é o texto como está impresso. O sistema acerta devolvendo `INVALID` (e o mesmo `raw`, se informado) |
| `NOT_FOUND` | Igual a `null` |
| `UNCERTAIN` | Aceito para compatibilidade com os `*.expected.json`; pontua como `VALID` (o valor é o que conta) |

`messages` lista códigos de `validationMessages` que o resultado precisa conter, como `DOCUMENT_EXPIRED` num
documento vencido. Se o valor está certo mas o código não veio, o campo conta como falso negativo.

**Campos que não aparecem em `fields` não são avaliados**, mesmo que o sistema os devolva. Anote todo campo que
importa, inclusive os ausentes (`null`), porque só assim o falso positivo aparece. Campos de sócios do contrato
social usam o caminho indexado, `partners[0].name`, e são agregados como `partners[].name`.

As amostras sintéticas seguem o mesmo formato (`samples/synthetic/documents/*.expected.json`) e servem de
exemplo completo; por isso a chave `normalized` é aceita.

## Como cada campo é pontuado

| Anotação | O sistema devolveu | Resultado |
|---|---|---|
| valor `V` | `V` normalizado | **TP** |
| valor `V` | outro valor | **FP** e **FN** |
| valor `V` | `NOT_FOUND`, ou `INVALID` sem valor | **FN** |
| `null` (ausente) | `NOT_FOUND` | **TN** |
| `null` (ausente) | qualquer valor | **FP** |
| `INVALID` (+ `raw`) | `INVALID` (+ mesmo `raw`) | **TP** |
| `INVALID` | `NOT_FOUND` | **FN** |
| `INVALID` | outro status | **FP** e **FN** |

Documento que não terminou (`FAILED`, `REJECTED`, tempo esgotado ou erro de upload): cada campo que existia no
papel é FN; os ausentes não pontuam nada.

- **Precision** = TP / (TP + FP). **Recall** = TP / (TP + FN). **F1** = média harmônica. Sem denominador, o valor
  é `—` (nunca 0).
- **Exact match** de um campo é o recall dele: fração dos campos que o documento traz que o sistema devolveu
  exatamente iguais.
- **Exatidão do DV**, só nos campos que dependem de dígito verificador (`cpf`, `cnpj`, `registrationNumber`,
  `holderDocument`, `holderCpf`, `partners[].cpf`, `mrz`): entre os campos que o sistema julgou, a fração em que
  o veredito (`VALID` ou `INVALID`) concorda com a anotação. Campo que o sistema nem leu conta à parte, como
  "sem veredito". Isto mede as regras de dígito; erro de OCR num dígito aparece como veredito errado, não como
  falha da regra.
- **Por tipo** soma os campos do tipo (micro-média). **Campos críticos** são os `required` do schema do tipo
  (`--critical TIPO=campo,campo` sobrescreve).
- **Por qualidade**: a mesma agregação por `tipo|qualidade`.
- **Classificação**: acerto do tipo detectado contra `documentType`, com matriz de confusão. A avaliação **não**
  envia `expectedDocumentType`, para medir a classificação sem dica.
- **Texto utilizável**: fração dos documentos legíveis concluídos cujo texto tem pelo menos `--min-text-chars`
  caracteres (padrão 20).
- **Latência**: mediana e máxima de ponta a ponta por tipo, com o sistema recebendo um documento por vez.

## Indicadores da PoC (PRD §3)

| Indicador | Limiar | Como é medido |
|---|---:|---|
| Documentos priorizados classificados corretamente | 85% | Classificação nos documentos dos sete tipos com extrator |
| Documentos legíveis com texto utilizável | 90% | Texto utilizável |
| Campos críticos com exact match | 90% | Recall dos campos críticos de todos os tipos |

O relatório marca cada um como atingido, não atingido ou sem dados. `--fail-on-indicators` faz o script sair com
código 1 quando algum não é atingido. O indicador "nenhum erro de OCR impede a consulta ao arquivo original" é
coberto pelos testes de integração, não por esta ferramenta.

## Como rodar

```bash
# 1. sistema de pé
docker compose up -d --build

# 2. avaliação (pasta fora do repositório)
python scripts/evaluate.py --dataset /dados/avaliacao --api http://localhost:8080 --out /dados/relatorio
```

Sem Python local:

```bash
docker run --rm --add-host=host.docker.internal:host-gateway -v "$PWD:/w" -v /dados/avaliacao:/dataset:ro \
  -v /dados/relatorio:/report -w /w python:3.12-slim \
  python scripts/evaluate.py --dataset /dataset --api http://host.docker.internal:8080 --out /report
```

Opções principais:

| Opção | Efeito |
|---|---|
| `--dataset PASTA` | Pasta com documentos e ground truth (obrigatória) |
| `--api URL` | API (padrão `http://localhost:8080`) |
| `--out PASTA` | Onde gravar `report.json` e `report.md` (padrão `./evaluation-report`, ignorada pelo git) |
| `--truth-suffix SUFIXO` | Sufixo do ground truth (padrão `.truth.json`) |
| `--limit N` | Só os N primeiros documentos |
| `--critical TIPO=a,b` | Campos críticos do tipo, no lugar dos `required` do schema |
| `--details` / `--include-values` | Identificação e valores no relatório (dado pessoal) |
| `--keep` | Não apaga os documentos da API |
| `--timeout S` | Espera máxima por documento (padrão 600 s) |
| `--baseline report.json` | Compara com uma rodada anterior |
| `--max-drop X` | Queda de F1 aceita contra o baseline (padrão 0,02) |
| `--fail-on-indicators` | Código 1 se algum indicador não for atingido |

Códigos de saída: `0` ok; `1` indicador não atingido (com `--fail-on-indicators`); `2` erro de uso (pasta
sem anotação, anotação inválida, API fora do ar); `3` regressão contra o baseline.

## Regressão a cada mudança de modelo, regra ou pré-processamento

Guarde o `report.json` de uma rodada aceita e passe-o como `--baseline` na seguinte. O script lista cada campo
cujo F1 caiu mais que `--max-drop`, e cada campo ou tipo que existia e sumiu, e sai com código 3. Trocar o
`OCR_MODEL_PROFILE` (ADR 0002), mexer num extrator ou num pré-processamento é exatamente quando rodar isso.

## Teste de fumaça com as amostras sintéticas

Confere que o framework, o sistema e as amostras estão de acordo. Com tudo de pé, todos os campos esperados devem
sair certos (F1 de 100%):

```bash
python scripts/evaluate.py --dataset samples/synthetic/documents --truth-suffix .expected.json \
  --api http://localhost:8080 --out .tmp/smoke --fail-on-indicators
```

Isto **não** valida o sistema em documento real. As amostras foram desenhadas junto com os extratores, e os
números delas só dizem que nada quebrou.

## Como montar um dataset de verdade

- Use documentos que você tem autorização para tratar, ou versões mascaradas. Nunca os coloque no repositório.
- Cubra cada tipo em mais de uma qualidade (digitalizado limpo, foto de celular, PDF, cópia escura) e use
  `quality` para separá-las; a média esconde o pior caso.
- Inclua documentos de outros estados, outras concessionárias e outras juntas comerciais: é onde os extratores
  vão errar, e o dataset sintético não os tem.
- Inclua documentos de tipos não suportados (`"documentType": "UNKNOWN"`) para medir o falso positivo da
  classificação, e documentos com defeitos reais (CPF com dígito errado, CNH vencida, campo ilegível).
- Anote os campos ausentes como `null`. Sem isso, um extrator que inventa valores não é penalizado.
- Um dataset de dezenas de documentos por tipo é o mínimo para os indicadores significarem alguma coisa; com
  poucos, uma amostra muda o percentual em pontos inteiros.
