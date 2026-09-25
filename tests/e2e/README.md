Testes ponta a ponta contra o compose de pé.

## `stage3_acceptance.py`

Envia uma amostra de cada tipo estruturado (`samples/synthetic/documents`, inclusive a `cnh-vencida`) por `POST /api/v1/documents`, espera
`COMPLETED` e confere, por campo, o valor normalizado e o status de `GET /documents/{id}/result` contra o
`<amostra>.expected.json`. Com `--web`, confere também que a tela de detalhe mostra os valores lidos. Só
biblioteca padrão; sai com código 0 se todos os tipos passarem.

```bash
docker compose up -d --build
docker run --rm -v "$PWD:/w" -w /w python:3.12-slim \
  python tests/e2e/stage3_acceptance.py \
  --api http://host.docker.internal:8080 --web http://host.docker.internal:3000
```

Cada linha do relatório traz o tempo de ponta a ponta e o do upload separados, porque o worker processa um
documento por vez: documentos enviados juntos esperam na fila e inflam o tempo de quem chega depois.

Ainda por escrever, do plano da Etapa 2 em diante: upload pela interface web e por `curl` em sequência,
listagem e filtros, exclusão, reinício do worker e falha do OCR com preservação do original. Os cenários de
queda do `ocr-service` e reprocessamento foram executados à mão no aceite da Etapa 2, sem script.
