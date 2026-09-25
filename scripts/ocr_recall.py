"""Recall de leitura por configuração do benchmark de latência (docs/bench/ocr-latency-*.json).

O benchmark original só confere valores-chave (CPF, CNPJ, totais): uma engine que perde linhas inteiras
de um parágrafo pode passar nele, porque as linhas com os valores-chave sobreviveram. Aqui cada linha ou
célula que o gerador desenhou é procurada no que a engine leu, e o recall é a fração encontrada.

A referência é o próprio texto de scripts/make-ocr-samples.py, então não há transcrição manual. Um item
conta como lido quando algum bloco reconhecido tem similaridade de pelo menos 0,9 com ele, sem acento e
sem caixa. O recall por caracteres pesa os itens pelo tamanho: perder uma cláusula inteira custa mais do
que perder uma célula de "QTD".

    docker run --rm --entrypoint python -v "$PWD:/w" -w /w docreader/ocr-service:stage2 scripts/ocr_recall.py
"""

from __future__ import annotations

import difflib
import importlib.util
import json
import sys
import unicodedata
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BENCH = ROOT / "docs" / "bench"
THRESHOLD = 0.9


def fold(text: str) -> str:
    decomposed = unicodedata.normalize("NFD", text)
    return "".join(ch for ch in decomposed if unicodedata.category(ch) != "Mn").lower().strip()


def load_generator():
    spec = importlib.util.spec_from_file_location("make_ocr_samples", ROOT / "scripts" / "make-ocr-samples.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)  # type: ignore[union-attr]
    return module


def reference(generator) -> dict[str, list[str]]:
    dense = [line for line in generator.PARAGRAPHS if line]
    table = [
        "DEMONSTRATIVO DE SERVICOS PRESTADOS",
        *generator.TABLE_HEADER,
        *[cell for row in generator.TABLE_ROWS for cell in row],
        "TOTAL GERAL",
        "4.896,00",
    ]
    card = [
        "REPUBLICA FEDERATIVA DO BRASIL",
        "MINISTERIO DA FAZENDA",
        "SECRETARIA DA RECEITA FEDERAL",
        "CADASTRO DE PESSOAS FISICAS",
        "NUMERO DE INSCRICAO",
        generator.CPF_PRIMARY,
        "NOME",
        "MARIA APARECIDA DA SILVA SOUZA",
        "NASCIMENTO",
        "14/03/1985",
    ]
    return {
        "pagina-texto-densa": dense,
        "pagina-tabela": table,
        "cpf-card": card,
    }


def reference_for(sample: str, references: dict[str, list[str]]) -> list[str] | None:
    for prefix, items in references.items():
        if sample.startswith(prefix):
            return items
    return None


def recall(items: list[str], texts: list[str]) -> tuple[float, float, list[str]]:
    folded = [fold(text) for text in texts]
    found_chars = 0
    total_chars = 0
    missing: list[str] = []
    found = 0

    for item in items:
        target = fold(item)
        total_chars += len(target)
        best = max((difflib.SequenceMatcher(None, target, candidate).ratio() for candidate in folded), default=0.0)
        # Um bloco que juntou vários itens (uma linha de tabela) contém o alvo inteiro.
        contained = any(target in candidate for candidate in folded)
        if best >= THRESHOLD or contained:
            found += 1
            found_chars += len(target)
        else:
            missing.append(item)

    return found / len(items), found_chars / total_chars, missing


def main() -> int:
    references = reference(load_generator())
    reports = sorted(BENCH.glob("ocr-latency-*.json"))
    if not reports:
        print("nenhum ocr-latency-*.json em docs/bench", file=sys.stderr)
        return 1

    print(f"{'configuração':<30}{'lado':>6}  {'amostra':<24}{'seg':>6}{'itens':>8}{'chars':>8}  perdidos")
    for path in reports:
        report = json.loads(path.read_text(encoding="utf-8"))
        for pipeline in report["pipelines"]:
            for measurement in pipeline["measurements"]:
                items = reference_for(measurement["sample"], references)
                if items is None:
                    continue

                by_item, by_char, missing = recall(items, measurement["texts"])
                seconds = sum(measurement["durations_ms"]) / len(measurement["durations_ms"]) / 1000
                shown = "; ".join(item[:38] for item in missing[:3]) + (" ..." if len(missing) > 3 else "")
                print(
                    f"{pipeline['pipeline']:<30}{report.get('maxSide', 0):>6}  {measurement['sample']:<24}"
                    f"{seconds:>6.1f}{by_item:>7.0%}{by_char:>8.0%}  {shown}"
                )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
