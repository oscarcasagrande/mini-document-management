"""Mede memória e latência do PP-StructureV3 numa página, isolado, em CPU (ADR 0002, adendo de 2026-09-29).

Roda dentro da imagem do ocr-service (que tem o extra `paddlex[ocr]`), com um volume de cache que já
tenha os modelos do PP-StructureV3 ou acesso à rede para baixá-los:

    docker run --rm --memory=6g --memory-swap=6g \
      -e PADDLE_PDX_CACHE_HOME=/models -v ocr-model-cache:/models \
      -v "$PWD/scripts:/s:ro" -v "$PWD/samples/synthetic/ocr:/samples:ro" \
      --entrypoint python docreader/ocr-service:local /s/ocr_structure_memory.py lean /samples/pagina-tabela.png

Configurações:
  lean  detector mobile + reconhecedor latino mobile, os mesmos do serviço (é o que app/structure.py usa)
  adr   padrões do PP-StructureV3 (detector e reconhecedor server), como medido no ADR 0002

oneDNN fica desligado (MKLDNN=true liga): com ele o PP-StructureV3 corrompe o heap na paddlepaddle 3.2.2.
O terceiro argumento opcional reduz o lado maior da imagem antes da inferência.

Imprime uma linha JSON: tamanho, megapixels, limite do cgroup, pico do cgroup, pico de RSS do processo,
tempos de carga e de duas inferências, tabelas e células encontradas.
"""

import json
import os
import resource
import sys
import time
from pathlib import Path

os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")
config = sys.argv[1]
image_path = sys.argv[2]
max_side = int(sys.argv[3]) if len(sys.argv) > 3 else 0
mkldnn = os.environ.get("MKLDNN", "false") == "true"
os.environ["FLAGS_use_mkldnn"] = "true" if mkldnn else "false"


def cgroup(name: str):
    try:
        raw = Path(f"/sys/fs/cgroup/{name}").read_text().strip()
        return raw if raw == "max" else int(raw)
    except OSError:
        return None


def mb(value):
    return round(value / 1024 / 1024) if isinstance(value, int) else None


import numpy as np  # noqa: E402
from paddleocr import PPStructureV3  # noqa: E402
from PIL import Image  # noqa: E402

options = dict(
    use_doc_orientation_classify=False,
    use_doc_unwarping=False,
    use_textline_orientation=False,
    use_formula_recognition=False,
    use_seal_recognition=False,
    use_chart_recognition=False,
    enable_mkldnn=mkldnn,
)
if config == "lean":
    options.update(text_detection_model_name="PP-OCRv5_mobile_det", text_recognition_model_name="latin_PP-OCRv5_mobile_rec")

started = time.perf_counter()
engine = PPStructureV3(**options)
load_s = time.perf_counter() - started

with Image.open(image_path) as source:
    image = source.convert("RGB")
if max_side and max(image.size) > max_side:
    ratio = max_side / max(image.size)
    image = image.resize((round(image.width * ratio), round(image.height * ratio)), Image.Resampling.LANCZOS)
bgr = np.asarray(image)[:, :, ::-1].copy()

timings, tables, cells = [], 0, 0
for _ in range(2):
    started = time.perf_counter()
    results = list(engine.predict(input=bgr))
    timings.append(round(time.perf_counter() - started, 2))
    for page in results:
        payload = page.json.get("res", page.json)
        tables = len(payload.get("table_res_list") or [])
        cells = sum(len(table.get("cell_box_list") or []) for table in payload.get("table_res_list") or [])

print(
    json.dumps(
        {
            "config": config,
            "mkldnn": mkldnn,
            "image": Path(image_path).name,
            "size": image.size,
            "megapixels": round(image.width * image.height / 1e6, 2),
            "memory_limit_mb": mb(cgroup("memory.max")),
            "load_s": round(load_s, 1),
            "predict_s": timings,
            "cgroup_peak_mb": mb(cgroup("memory.peak")),
            "process_maxrss_mb": round(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024),
            "tables": tables,
            "table_cells": cells,
        }
    )
)
