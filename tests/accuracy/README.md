Testes do framework de avaliação (`scripts/evaluate.py`). O formato do ground truth, as métricas e o uso da
ferramenta estão em [`docs/evaluation.md`](../../docs/evaluation.md).

Estes testes cobrem a **ferramenta**: a leitura do ground truth, a pontuação de cada campo, a agregação, os
indicadores, o baseline, as proteções de dado real e uma execução completa contra uma API falsa local. Não precisam
do sistema no ar e não usam documento nenhum.

```bash
docker run --rm -v "$PWD:/w" -w /w python:3.12-slim sh -c \
  "pip install -q pytest && python -m pytest tests/accuracy -q"
```

A medição do **sistema** contra documentos é o próprio `scripts/evaluate.py`; o teste de fumaça com as amostras
sintéticas está em `docs/evaluation.md`.
