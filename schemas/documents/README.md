# Schemas por tipo documental

Um JSON Schema por tipo estruturado, versionado por `$id` (`BR_CIN.v1`). O nome do campo no schema é o
caminho do campo em `GET /api/v1/documents/{id}/result`. Campo não encontrado é sempre `null`; o extrator
nunca inventa valor (RF-011).

| Tipo | Schema | Extrator | Campos | Validações determinísticas |
|---|---|---|---|---|
| Cartão de CPF | `BR_CPF_CARD.v1.json` | `BrCpfCardExtractor` | cpf, name, birthDate | DV do CPF, data |
| CIN / RG | `BR_CIN.v1.json` | `BrCinExtractor` | name, cpf, rg, birthDate, issueDate, expirationDate, birthPlace, fatherName, motherName, mrz | DV do CPF, datas, validade vencida, formato do RG, MRZ (ICAO 9303 TD1) |
| CNH | `BR_CNH.v1.json` | `BrCnhExtractor` | name, cpf, birthDate, registrationNumber, category, firstLicenseDate, issueDate, expirationDate | DV do CPF, **DV do registro**, datas, validade vencida, categoria |
| Comprovante de residência | `BR_PROOF_OF_ADDRESS.v1.json` | `BrProofOfAddressExtractor` | holderName, holderDocument, addressLine, neighborhood, city, state, postalCode, referenceMonth, dueDate, serviceType | DV de CPF/CNPJ, CEP, UF, competência |
| Cartão CNPJ | `BR_CNPJ_CARD.v1.json` | `BrCnpjCardExtractor` | cnpj, openingDate, legalName, tradeName, mainActivityCode/Description, legalNature, endereço, situação | DV do CNPJ **alfanumérico**, datas, CEP, UF |
| CCMEI | `BR_CCMEI.v1.json` | `BrCcmeiExtractor` | cnpj, legalName, tradeName, openingDate, shareCapital, atividade, address, postalCode, city, state, holderName, holderCpf, holderBirthDate, certificateDate | DV do CNPJ e do CPF, datas, CEP, UF, valor em reais |
| Contrato social | `BR_SOCIAL_CONTRACT.v1.json` | `BrSocialContractExtractor` | companyName, cnpj, nire, shareCapital, headquarters, headquartersPostalCode, corporatePurpose, contractDate, `partners[N].name`, `partners[N].cpf` | DV do CNPJ e dos CPFs dos sócios, CEP, valor, data por extenso |

## Como cada tipo é escrito

- **Classificação** (`x-docreader.classification`): pontuação por evidência, espelhando `DocumentTypeProfile`.
  Cada evidência (`evidence`) tem um nome, um peso e padrões alternativos (qualquer um vale, uma vez); a
  contra-evidência (`counterEvidence`) subtrai o seu peso. A pontuação é a soma, entre 0 e 1, e o tipo é aceito
  quando atinge o `threshold`. Nenhuma evidência é obrigatória: um título cortado ainda deixa o documento ser
  reconhecido pelos campos ao redor, e o título sozinho não basta nos tipos ambíguos. O casamento ignora
  acento e caixa, aceita palavras coladas ou partidas pelo OCR e tolera um erro de letra a cada oito (até
  três); termos curtos como `CEP` e `CPF` só valem como palavra inteira. `Stage3ClassificationTests` falha se o
  schema e o código divergirem em nomes, pesos, padrões ou limiar.
- **Extração**: rótulo e vizinhança. `LineSearch` procura o valor na mesma linha (`Rótulo: valor`), à direita
  ou abaixo do rótulo pela geometria das caixas do OCR, e na ordem de leitura só quando o provedor não
  mandou coordenadas. Blocos partidos na mesma linha visual são juntos, e uma linha que é outro rótulo
  conhecido nunca vira valor. Contrato é texto corrido: as linhas são juntas e o valor sai de expressões
  ("com sede na", "capital social de").
- **Confiança**: a do OCR na linha do valor, menos 0,05 por linha de distância ao rótulo, menos 0,30 (com o
  aviso `NO_LABEL_NEARBY`) quando o valor foi achado sem rótulo.
- **Status**: `VALID`, `INVALID` (leu e reprovou, com o valor lido preservado), `NOT_FOUND`, `UNCERTAIN`
  (`fatherName`/`motherName` num bloco de filiação, cuja ordem é suposta; `mrz` com nascimento diferente do
  impresso; `serviceType` ambíguo).

## O que não é validado

- **Nada foi medido em documento real.** Os extratores foram escritos e testados sobre amostras sintéticas
  (`samples/synthetic/documents`) desenhadas com a estrutura de rótulo e valor dos documentos reais, mas não
  copiadas deles. Layouts de outros estados, de outras concessionárias e de contratos de outras juntas
  vão errar de formas que esta suíte não vê; a Etapa 4 (avaliação) é quem mede isso.
- O DV do registro da CNH segue o algoritmo do DENATRAN como as bibliotecas de validação o reproduzem
  (`CnhRegistration`); a PoC não consulta a base oficial, então um número com DV certo pode não existir.
- O RG não tem dígito verificador nacional: valida-se o formato.
- **Validade vencida** (CIN e CNH) não reprova o campo: a data continua `VALID`, com a mensagem
  `DOCUMENT_EXPIRED` em `validationMessages`. É quem consome o resultado que decide o que fazer com ela.
- Documento com frente e verso na mesma imagem funciona; em arquivos separados são dois documentos.
