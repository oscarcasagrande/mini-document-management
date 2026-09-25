"""Mede latência por página do PP-OCRv5 puro contra o PP-StructureV3, em CPU.

A decisão de engine da Etapa 2 depende de saber quanto custa a mais a pipeline de layout e o que
ela entrega em troca. Este script roda as duas sobre as mesmas amostras e reporta:

  - tempo de carga de modelo (custo único, pago no start do contêiner);
  - latência por página em regime, com mediana e p95 sobre N repetições;
  - pico de memória residente do processo;
  - o que cada pipeline conseguiu ler: número de blocos de texto, caracteres e, no caso do
    StructureV3, tabelas reconhecidas.

A última coluna é a que impede uma comparação enviesada: medir só latência faria o PP-OCRv5 parecer
sempre melhor, já que o custo extra do StructureV3 existe justamente para produzir estrutura.

    python ocr_benchmark.py --samples /samples --repeat 3
"""

from __future__ import annotations

import argparse
import json
import platform
import resource
import statistics
import sys
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Callable


@dataclass
class PageMeasurement:
    sample: str
    pipeline: str
    width: int
    height: int
    megapixels: float
    durations_ms: list[float] = field(default_factory=list)
    text_blocks: int = 0
    characters: int = 0
    tables: int = 0
    texts: list[str] = field(default_factory=list)
    scores: list[float] = field(default_factory=list)
    expected: dict[str, dict[str, Any]] = field(default_factory=dict)
    error: str | None = None

    @property
    def median_ms(self) -> float:
        return statistics.median(self.durations_ms) if self.durations_ms else 0.0

    @property
    def p95_ms(self) -> float:
        if not self.durations_ms:
            return 0.0
        ordered = sorted(self.durations_ms)
        index = min(len(ordered) - 1, int(round(0.95 * (len(ordered) - 1))))
        return ordered[index]

    @property
    def ms_per_megapixel(self) -> float:
        return self.median_ms / self.megapixels if self.megapixels else 0.0


def peak_rss_mb() -> float:
    # ru_maxrss vem em kilobytes no Linux.
    return resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024.0


def image_size(path: Path) -> tuple[int, int]:
    from PIL import Image

    with Image.open(path) as image:
        return image.width, image.height


def count_ocr_output(result: Any) -> tuple[int, int, int]:
    """Extrai blocos, caracteres e tabelas de uma saída do PaddleOCR 3.x."""
    blocks = 0
    characters = 0
    tables = 0

    for page in result or []:
        payload = getattr(page, "json", None)
        if isinstance(payload, dict):
            payload = payload.get("res", payload)
        elif isinstance(page, dict):
            payload = page.get("res", page)
        else:
            payload = {}

        texts = payload.get("rec_texts") or []
        blocks += len(texts)
        characters += sum(len(str(text)) for text in texts)

        # PP-StructureV3 devolve os blocos de layout em parsing_res_list.
        for item in payload.get("parsing_res_list") or []:
            label = str(item.get("block_label", "")).lower()
            if label == "table":
                tables += 1
            content = item.get("block_content")
            if content and not texts:
                blocks += 1
                characters += len(str(content))

        for key in ("table_res_list", "table_result"):
            entries = payload.get(key)
            if isinstance(entries, list):
                tables = max(tables, len(entries))

    return blocks, characters, tables


def collect_text(result: Any) -> tuple[list[str], list[float]]:
    """Texto reconhecido e confiança por bloco, na ordem em que o PaddleOCR devolveu."""
    texts: list[str] = []
    scores: list[float] = []

    for page in result or []:
        payload = getattr(page, "json", None)
        if isinstance(payload, dict):
            payload = payload.get("res", payload)
        elif isinstance(page, dict):
            payload = page.get("res", page)
        else:
            payload = {}

        page_texts = payload.get("rec_texts") or []
        page_scores = payload.get("rec_scores") or []
        texts.extend(str(text) for text in page_texts)
        scores.extend(round(float(score), 4) for score in page_scores)

    return texts, scores


# Valores que cada amostra sintética carrega, para medir exatidão e não só contagem de blocos.
# Os valores vêm de scripts/make-ocr-samples.py; o casamento é por prefixo do nome da amostra.
EXPECTED_VALUES: dict[str, dict[str, str]] = {
    "cpf-card-": {
        "cpf": "111.444.777-35",
        "name": "MARIA APARECIDA DA SILVA SOUZA",
        "birthDate": "14/03/1985",
    },
    "pagina-texto-densa": {
        "cpf": "111.444.777-35",
        "cnpj": "12.ABC.345/01DE-35",
    },
    "pagina-tabela": {
        "total001": "216,00",
        "total003": "989,00",
    },
}


def check_expected(sample: str, texts: list[str]) -> dict[str, dict[str, Any]]:
    """
    Confere cada valor esperado. `exact` significa que a string aparece, byte a byte, dentro de algum
    bloco reconhecido; `found_in` guarda o bloco em que apareceu, ou o bloco mais parecido quando não.
    """
    import difflib

    expected: dict[str, str] = {}
    for prefix, values in EXPECTED_VALUES.items():
        if sample.startswith(prefix):
            expected = values
            break

    checked: dict[str, dict[str, Any]] = {}
    for name, value in expected.items():
        containing = next((text for text in texts if value in text), None)
        if containing is not None:
            checked[name] = {"expected": value, "exact": True, "found_in": containing}
            continue

        closest = difflib.get_close_matches(value, texts, n=1, cutoff=0.0)
        checked[name] = {"expected": value, "exact": False, "found_in": closest[0] if closest else None}

    return checked


def environment_info() -> dict[str, Any]:
    """Versões e limites que explicam os números, para o relatório ser reproduzível."""
    import os
    from importlib import metadata

    def version(package: str) -> str | None:
        try:
            return metadata.version(package)
        except metadata.PackageNotFoundError:
            return None

    memory_limit = None
    for path in ("/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory/memory.limit_in_bytes"):
        try:
            raw = Path(path).read_text().strip()
            memory_limit = None if raw == "max" else int(raw)
            break
        except (OSError, ValueError):
            continue

    return {
        "python": platform.python_version(),
        "paddlepaddle": version("paddlepaddle"),
        "paddleocr": version("paddleocr"),
        "paddlex": version("paddlex"),
        "flags_use_mkldnn": os.environ.get("FLAGS_use_mkldnn"),
        "omp_num_threads": os.environ.get("OMP_NUM_THREADS"),
        "cpu_count": os.cpu_count(),
        "memory_limit_bytes": memory_limit,
    }


def mkldnn_enabled() -> bool:
    """oneDNN fica desligado por padrão: na build 3.3.1 de CPU ele derruba toda inferência."""
    import os

    return os.environ.get("FLAGS_use_mkldnn", "false").strip().lower() in {"1", "true", "yes", "on"}


def build_ppocr(
    version: str,
    detection_model: str | None = None,
    recognition_model: str | None = None,
) -> Callable[[], Callable[[Any], Any]]:
    """
    Pipeline de detecção + reconhecimento. `enable_mkldnn` segue `FLAGS_use_mkldnn`, que tem
    precedência sobre o parâmetro: com o valor padrão (desligado) tudo se comporta como na
    medição original, e o experimento de oneDNN liga a variável.
    """

    def factory() -> Callable[[Any], Any]:
        from paddleocr import PaddleOCR

        options: dict[str, Any] = {
            "use_doc_orientation_classify": False,
            "use_doc_unwarping": False,
            "use_textline_orientation": False,
            "lang": "pt",
            "ocr_version": version,
            "enable_mkldnn": mkldnn_enabled(),
        }
        if detection_model:
            options["text_detection_model_name"] = detection_model
        if recognition_model:
            options["text_recognition_model_name"] = recognition_model

        engine = PaddleOCR(**options)
        return lambda frame: engine.predict(input=frame)

    return factory


def build_structure() -> Callable[[Any], Any]:
    from paddleocr import PPStructureV3

    engine = PPStructureV3(
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_formula_recognition=False,
        use_seal_recognition=False,
        use_chart_recognition=False,
        enable_mkldnn=False,
    )
    return lambda frame: engine.predict(input=frame)


# Quatro configurações, não duas. O pedido citou "PP-OCRv5 puro", mas o paddleocr 3.7 já entrega o
# PP-OCRv6 como padrão, e o detector server que o lang=pt escolhe por padrão é pesado para CPU — a
# variante mobile é a candidata realista. Medir as quatro deixa a escolha baseada em dado.
# Ordenado do mais barato ao mais caro: se a execução for interrompida, os dados que sobram ainda
# respondem a pergunta principal.
PIPELINES: dict[str, Callable[[], Callable[[Any], Any]]] = {
    "PP-OCRv5 (det mobile)": build_ppocr("PP-OCRv5", "PP-OCRv5_mobile_det"),
    "PP-OCRv6 (padrao 3.7)": build_ppocr("PP-OCRv6"),
    # O PP-OCRv6 tem três tamanhos; o padrão do paddleocr 3.7 é o medium. As variantes menores entram
    # como hipótese para a meta de latência da Etapa 2 (docs/adr/0002).
    "PP-OCRv6 medium": build_ppocr("PP-OCRv6", "PP-OCRv6_medium_det", "PP-OCRv6_medium_rec"),
    "PP-OCRv6 small": build_ppocr("PP-OCRv6", "PP-OCRv6_small_det", "PP-OCRv6_small_rec"),
    "PP-OCRv6 tiny": build_ppocr("PP-OCRv6", "PP-OCRv6_tiny_det", "PP-OCRv6_tiny_rec"),
    "PP-OCRv5 (det server)": build_ppocr("PP-OCRv5"),
    "PP-StructureV3": build_structure,
}


def load_frame(path: Path, max_side: int) -> Any:
    """
    O que o ocr-service entrega ao motor: imagem RGB, reduzida por `app.pages._limit_side` (o mesmo
    código da produção) quando passa de `max_side`, em BGR como o PaddleOCR espera. Decodificação e
    redução entram no tempo medido, porque na produção elas também estão no caminho da página.
    """
    import numpy as np
    from PIL import Image

    sys.path.insert(0, "/app")
    from app.pages import _limit_side  # noqa: PLC0415 - só existe dentro da imagem do ocr-service

    with Image.open(path) as source:
        image = _limit_side(source.convert("RGB"), max_side)

    return np.asarray(image)[:, :, ::-1].copy()


def run_pipeline(name: str, samples: list[Path], repeat: int, max_side: int = 0) -> dict[str, Any]:
    print(f"\n=== {name}: carregando modelos ===", flush=True)

    load_started = time.perf_counter()
    try:
        predict = PIPELINES[name]()
    except Exception as exception:  # noqa: BLE001 - relatar e seguir para a outra pipeline
        print(f"  FALHA ao carregar: {exception}", flush=True)
        return {"pipeline": name, "load_error": str(exception), "measurements": []}

    load_ms = (time.perf_counter() - load_started) * 1000
    print(f"  carga concluída em {load_ms / 1000:.1f}s", flush=True)

    measurements: list[PageMeasurement] = []

    for path in samples:
        width, height = image_size(path)
        measurement = PageMeasurement(
            sample=path.name,
            pipeline=name,
            width=width,
            height=height,
            megapixels=round(width * height / 1_000_000, 3),
        )

        # Primeira passada é descartada: ela paga caches internos e alocação de buffers.
        try:
            predict(load_frame(path, max_side))
        except Exception as exception:  # noqa: BLE001
            measurement.error = str(exception)
            measurements.append(measurement)
            print(f"  {path.name:<34} ERRO: {exception}", flush=True)
            continue

        for _ in range(repeat):
            started = time.perf_counter()
            result = predict(load_frame(path, max_side))
            elapsed_ms = (time.perf_counter() - started) * 1000
            measurement.durations_ms.append(elapsed_ms)

        blocks, characters, tables = count_ocr_output(result)
        measurement.text_blocks = blocks
        measurement.characters = characters
        measurement.tables = tables
        measurement.texts, measurement.scores = collect_text(result)
        measurement.expected = check_expected(path.name, measurement.texts)
        measurements.append(measurement)

        for field_name, outcome in measurement.expected.items():
            print(f"    {field_name:<10} {'EXATO' if outcome['exact'] else 'DIVERGE'}", flush=True)

        print(
            f"  {path.name:<34} {measurement.median_ms:>8.0f} ms  "
            f"({measurement.ms_per_megapixel:>6.0f} ms/MP)  "
            f"blocos={blocks:<4} chars={characters:<6} tabelas={tables}",
            flush=True,
        )

    return {
        "pipeline": name,
        "load_ms": round(load_ms, 1),
        "max_side": max_side,
        "peak_rss_mb": round(peak_rss_mb(), 1),
        "measurements": [asdict(m) | {"median_ms": round(m.median_ms, 1), "p95_ms": round(m.p95_ms, 1)} for m in measurements],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Benchmark de OCR em CPU")
    parser.add_argument("--samples", default="/samples", help="Diretório com as amostras")
    parser.add_argument("--repeat", type=int, default=3, help="Repetições por amostra em regime")
    parser.add_argument("--output", default="/out/ocr-benchmark.json", help="Arquivo JSON de saída")
    parser.add_argument("--only", help="Roda apenas uma pipeline")
    parser.add_argument(
        "--max-side",
        type=int,
        default=0,
        help="Reduz o lado maior para no máximo N pixels antes do OCR (0 = sem redução)",
    )
    parser.add_argument("--samples-only", help="Lista de nomes de amostra separados por vírgula")
    args = parser.parse_args()

    sample_dir = Path(args.samples)
    samples = sorted(path for path in sample_dir.glob("*.png"))
    if args.samples_only:
        wanted = {name.strip() for name in args.samples_only.split(",")}
        samples = [path for path in samples if path.name in wanted]
    if not samples:
        print(f"Nenhuma amostra .png em {sample_dir}", file=sys.stderr)
        return 1

    print(f"CPU: {platform.processor() or platform.machine()} | amostras: {len(samples)} | repetições: {args.repeat}")

    names = [args.only] if args.only else list(PIPELINES)
    report = {
        "generatedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "repeat": args.repeat,
        "maxSide": args.max_side,
        "sampleCount": len(samples),
        "environment": environment_info(),
        "pipelines": [run_pipeline(name, samples, args.repeat, args.max_side) for name in names],
    }

    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"\nRelatório em {output}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
