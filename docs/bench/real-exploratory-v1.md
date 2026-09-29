# Teste exploratório com documentos reais (v1)

Quatro documentos reais (uma CNH, um comprovante de residência e dois contratos sociais), enviados uma vez
cada pela API, sem ground truth. O objetivo **não é medir F1** — quatro documentos não sustentam um número —
é descobrir onde o sistema quebra em documento de verdade e o que cada quebra custaria para corrigir. Para
acurácia de verdade, ver `docs/evaluation.md` e `scripts/evaluate.py` contra um dataset maior.

**Os arquivos originais não entram no repositório** (ficaram em `C:\datasets\docreader-real-v1`, fora dele,
como manda `docs/evaluation.md`). Este relatório não traz nome de pessoa, CPF, CNPJ nem endereço — os campos
são descritos por rótulo e status, nunca por valor. Os nomes de arquivo usados abaixo (ex.: `ELEKTRO.PDF.pdf`)
identificam só o tipo de documento e a concessionária emissora, não o titular.

## Como foi feito

- `docker compose up -d --build` (compose de aceite, sem overlay — fila PostgreSQL, ADR 0001, configuração
  padrão do `.env.example`).
- Os quatro arquivos enviados via `POST /api/v1/documents`, um de cada vez, e acompanhados até o estado final
  (`GET .../status`).
- Para cada um: `GET /api/v1/documents/{id}` (metadados, linha do tempo com duração por página),
  `GET .../result` (classificação e campos), `GET .../classification-diagnostics` (quando o tipo saiu
  `UNKNOWN` ou perto do limiar) e `GET .../extraction-diagnostics` (motivo de cada campo que não veio).
- **Nenhum extrator nem regra de classificação foi alterado nesta rodada.** Nenhuma correção de infraestrutura
  foi necessária: os quatro documentos foram aceitos, processaram e chegaram a `COMPLETED` sem erro de upload,
  worker travado ou timeout — não houve nada para consertar antes de coletar o retrato.

## Retrato por documento

### 1. CNH (`CNHE.PDF.pdf`)

| Aspecto | Resultado |
|---|---|
| Aceite | Aceito (202). PDF, 294 KB, 1 página — dentro dos limites |
| Fonte do texto | Camada nativa do PDF (RF-009), não OCR — 0,4 s |
| Classificação | `UNKNOWN`. Candidato mais próximo: `BR_CNH`, score 0,25 de 0,60 |
| Campos | Nenhum extraído (documento `UNKNOWN`; nenhum extrator se aplica) |

Este é o achado mais importante da rodada. O PDF é uma **CNH digital** (assinada digitalmente, com QR-code,
gerada pelo aplicativo oficial), não um scan/foto da carteira física. A camada de texto nativa do PDF passa no
limiar de suficiência do RF-009 (mínimo de 20 caracteres alfanuméricos), mas o que ela contém é só o cabeçalho
padrão do documento — menção à República Federativa, ao Ministério dos Transportes/SENATRAN, ao QR-code e à
validação por certificado digital. Nenhum dado da pessoa (nome, CPF, categoria, datas, número de registro) está
nessa camada: no PDF gerado pelo app oficial, os campos do titular ficam renderizados como imagem, não como
texto selecionável. O `classification-diagnostics` mostra exatamente isso: das evidências de `BR_CNH`, só
"republic", "issuing-authority" e "validity" (presentes no cabeçalho) foram achadas; título, categoria,
filiação, número de registro, documento de identidade, primeira habilitação e CPF — tudo o que depende dos
campos da pessoa — ficou de fora. O sistema não teve como saber que a camada nativa era insuficiente: ela
"parece" texto de verdade, só não é o texto que importa.

### 2. Comprovante de residência (`ELEKTRO.PDF.pdf`)

| Aspecto | Resultado |
|---|---|
| Aceite | Aceito (202). PDF, 647 KB, 2 páginas |
| Fonte do texto | OCR (PaddleOCR) — página 1: 88,0 s (254 blocos); página 2: 44,5 s (91 blocos) |
| Classificação | `BR_PROOF_OF_ADDRESS`, confiança 1,00 |
| Campos | 8 de 10 lidos e válidos; 1 lido com **valor errado e status VALID**; 1 rejeitado por formato; 1 ausente do documento |

O documento é uma nota fiscal de energia elétrica (DANFE), que o classificador aceita corretamente como
comprovante de residência. `endereço`, `CEP`, `cidade`, `UF`, `data de vencimento`, `documento do titular`
(CNPJ, dígito verificador válido) e `tipo de serviço` saíram certos. Dois problemas:

- **`nome do titular` veio com status `VALID` mas o valor é claramente errado** — o
  `extraction-diagnostics` mostra que a busca pelo rótulo "NOME DO CLIENTE" aceitou como valor uma linha mais
  abaixo no layout, que na verdade é o número/série/data de emissão da nota fiscal, não o nome do cliente. Um
  campo assim é pior que `NOT_FOUND` (a mesma lição já registrada em CLAUDE.md sobre o `birthPlace` do RIC):
  ninguém vê motivo para desconfiar de um campo `VALID`.
- **`mês de referência` foi rejeitado**: o rótulo foi achado, mas os três candidatos de valor examinados foram
  o próprio texto do rótulo, o mês por extenso (formato "Nome do mês/ano") e uma linha de classificação fiscal
  não relacionada — nenhum bateu com o formato numérico que o extrator espera.
- `bairro` ficou `NOT_FOUND` porque nenhum dos rótulos procurados (`BAIRRO`, `BAIRRO/DISTRITO`) aparece neste
  layout de DANFE — pode ser que o documento genuinamente não repita essa informação como campo isolado.

### 3. Contrato social 1 — Consolidação (`CONSOLIDACAO.16.12.25.PDF.pdf`)

| Aspecto | Resultado |
|---|---|
| Aceite | Aceito (202). PDF, 6,3 MB, 11 páginas |
| Fonte do texto | OCR, todas as 11 páginas rotacionadas 180° e corrigidas automaticamente (RF-009) — 25,0 a 41,9 s por página (275,7 s no total) |
| Classificação | `BR_SOCIAL_CONTRACT`, confiança 1,00 |
| Campos | 4 de 8 lidos e válidos (mais um com valor levemente sujo); 4 ausentes; **nenhum sócio extraído** |

`CNPJ` (dígito verificador válido), `NIRE`, `data do contrato` e `razão social` foram lidos; a razão social
veio com uma palavra solta grudada na frente do nome (mais fácil de corrigir que o achado 5 abaixo, mas ainda
assim um valor levemente errado, não exatamente o que está impresso). `objeto social`, `sede`, `CEP da sede` e
`capital social` ficaram `NOT_FOUND` — o `extraction-diagnostics` diz que o padrão de texto não bateu em
nenhum lugar do documento (`PATTERN_NOT_FOUND`), não que faltou rótulo.

**O achado mais sério deste documento: nenhum sócio foi extraído.** `partners[]` saiu completamente vazio num
documento de 11 páginas que decerto lista sócios — o mesmo extrator leu 2 sócios perfeitamente no documento 4
abaixo. Como CPF e contrato social não passam pelo `LineSearch`/`ExtractionTrace` (nota já registrada em
CLAUDE.md), o `extraction-diagnostics` não explica por quê: não há rastro de tentativa para depurar. A rotação
de 180° em todas as páginas é a diferença mais visível entre este documento e o outro contrato — é candidata a
causa, mas não confirmada.

### 4. Contrato social 2 — Última alteração contratual (`ULT.ALTERAOCONTRATUAL.PDF.pdf`)

| Aspecto | Resultado |
|---|---|
| Aceite | Aceito (202). PDF, 2,6 MB, 6 páginas |
| Fonte do texto | OCR, sem rotação necessária — 18,3 a 51,1 s por página (230,5 s no total) |
| Classificação | `BR_SOCIAL_CONTRACT`, confiança 1,00 |
| Campos | 6 de 8 lidos e válidos; 2 sócios lidos (nome + CPF, dígito verificador correto nos dois); 2 campos ausentes |

Este é o resultado mais limpo da rodada: `CNPJ`, `razão social`, `data do contrato`, `sede`, `CEP da sede` e
`capital social` corretos, e os **dois sócios com nome e CPF, os dois com dígito verificador válido** — prova
de que a extração de sócios funciona em documento real, não só na amostra sintética. `objeto social` ficou
`NOT_FOUND` pelo mesmo motivo do documento 3 (padrão não bateu); `NIRE` também ficou de fora — pode ser que
uma alteração contratual pontual não repita o NIRE do registro original, em vez de ser uma falha de leitura.

## Falhas encontradas, da mais barata para a mais cara de corrigir

1. **[Baixo]** `mês de referência` do comprovante de residência não reconhece o mês por extenso
   ("Nome do mês/ano"), só o formato numérico. Mudança local: ensinar o leitor de valor desse campo a
   reconhecer nomes de mês em português, com uma tabela de conversão.
2. **[Baixo]** `NIRE` ausente na alteração contratual (documento 4). Pode ser que o documento genuinamente não
   repita o campo — conferir isso antes de tratar como bug; se for um rótulo alternativo não coberto, é uma
   mudança tão pontual quanto o item 1.
3. **[Baixo-médio, prioridade alta]** `nome do titular` do comprovante de residência captura o texto da nota
   fiscal em vez do nome do cliente, com status `VALID`. É um ajuste pontual na busca pelo rótulo "NOME DO
   CLIENTE" neste layout (alcance errado ou falta de guarda contra candidato que contém texto de nota fiscal),
   mas prioridade alta apesar do esforço baixo: é exatamente o padrão que CLAUDE.md já registra como pior que
   `NOT_FOUND` — ninguém desconfia de um campo com status `VALID`.
4. **[Médio]** `objeto social` nunca foi encontrado em nenhum dos dois contratos sociais reais (documentos 3 e
   4). O padrão do `ProseIndex` foi calibrado só com a amostra sintética; precisa olhar a redação real de
   "objeto social" nesses dois documentos e ampliar o padrão — o mesmo tipo de trabalho já feito para CNH/RIC
   na Etapa 5, ainda pendente para contrato social.
5. **[Médio]** A CNH digital (documento 1) tem camada nativa que passa no limiar de 20 caracteres do RF-009
   mas só contém cabeçalho, não os campos da pessoa — resultado silencioso: `UNKNOWN`, zero campos, nenhum
   aviso. Precisa de uma segunda heurística de suficiência da camada nativa (não só contagem de caracteres) ou
   de um retrocesso automático para OCR completo quando a classificação sobre a camada nativa fica muito abaixo
   do limiar.
6. **[Médio-alto]** `partners[]` zerado no contrato social de 11 páginas (documento 3), mas correto no de 6
   páginas (documento 4) — mesmo extrator, mesma versão. `sede`, `CEP da sede` e `capital social` também
   falharam só no documento 3, o que sugere causa comum. Duas hipóteses a investigar antes de corrigir: a
   rotação de 180° em todas as páginas degradou a leitura o bastante para o `ProseIndex` não reconhecer o
   padrão, ou uma "consolidação" lista sócios de um jeito estruturalmente diferente de uma alteração pontual.

**Observação à parte, não uma falha de extração:** a latência por página nos quatro documentos reais variou de
8,7 s a 88,0 s, a maior parte acima do alvo de ≤18 s/página do ADR 0002. Medido nesta máquina com vários outros
processos rodando ao mesmo tempo, não numa bancada isolada — não é uma regressão confirmada, mas vale remedir
em ambiente controlado antes de tirar conclusão.

## O que funcionou bem

- Classificação certa em 3 dos 4 documentos, com 100% de confiança — o quarto (CNH) é um caso legítimo de
  camada nativa insuficiente, não um erro de regra de classificação.
- Todo dígito verificador conferido saiu correto: CNPJ nos dois contratos e no comprovante, CPF dos dois
  sócios do documento 4.
- Extração de sócios (`partners[].name`, `partners[].cpf`) funcionou perfeitamente em documento real quando o
  documento não tinha a complicação da rotação.
- RF-009 (rotação automática) corrigiu corretamente as 11 páginas invertidas do documento 3 — o documento
  processou e classificou certo apesar de ter chegado de cabeça para baixo.
- Nenhuma rejeição de upload, nenhum `FAILED`, nenhum timeout: a infraestrutura de ingestão e fila não
  precisou de nenhum ajuste para os quatro documentos chegarem a um estado final.
