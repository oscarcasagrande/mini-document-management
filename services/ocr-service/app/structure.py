"""PP-StructureV3 on demand, for pages PP-OCRv5 read as a low-confidence table (RF-009).

Runs in a **child process**, never in the service process, for three measured reasons (ADR 0002,
addendum of 2026-09-29):

- **oneDNN.** With oneDNN on, PP-StructureV3 on paddlepaddle 3.2.2 corrupts the heap
  (``malloc(): unsorted double linked list corrupted``) and segfaults or hangs. The service runs
  PP-OCRv5 with oneDNN on, and ``FLAGS_use_mkldnn`` is read once per process, so only another
  process can run with it off.
- **Memory.** The child peaks at 2.8-2.9 GB on an A4 page at 200 DPI, on top of the ~2.3 GB of the
  service. It marks itself as the first thing the kernel kills on out-of-memory
  (``oom_score_adj=1000``), so a page that does not fit costs the child, and the page falls back to
  the PP-OCRv5 result it already has, instead of costing the container.
- **Timeouts.** A child can be killed at the page deadline; an inference thread cannot.

Models: the pipeline is configured with the same mobile detector and Latin recognizer the service
uses. PP-StructureV3's defaults (server detector and recognizer) are what ran out of memory in ADR
0002 at 6 GiB; with the mobile pair the same page fits in 2.9 GB. Its layout and table models (~873 MB)
are not in the image: they are downloaded into ``OCR_MODEL_CACHE_DIR`` by a background prefetch at
startup, only when the feature is on, and a page that arrives before the prefetch finished is simply
not sent to PP-StructureV3.

The worker entry point is this module: ``python -m app.structure prefetch`` or
``python -m app.structure predict <image> <output.json>``. It writes nothing to stdout that the
service reads; the result is a JSON file, written whole or not at all.
"""

from __future__ import annotations

import json
import os
import signal
import subprocess
import sys
import tempfile
import threading
from collections.abc import Sequence
from pathlib import Path
from typing import Any, Protocol

from PIL import Image

from .engine import EngineOutput, RecognizedBlock, flatten_polygon, to_jsonable

# The models PP-StructureV3 creates with the options below, as observed on paddleocr 3.7.0 (the
# pipeline logs each one it creates). The two PP-OCRv5 models are already in the image.
STRUCTURE_MODELS: tuple[str, ...] = (
    "PP-DocBlockLayout",
    "PP-DocLayout_plus-L",
    "PP-LCNet_x1_0_table_cls",
    "SLANeXt_wired",
    "SLANet_plus",
    "RT-DETR-L_wired_table_cell_det",
    "RT-DETR-L_wireless_table_cell_det",
    "PP-LCNet_x1_0_doc_ori",
    "PP-LCNet_x1_0_textline_ori",
    "PP-OCRv5_mobile_det",
    "latin_PP-OCRv5_mobile_rec",
)

PIPELINE_OPTIONS: dict[str, Any] = {
    "use_doc_orientation_classify": False,
    "use_doc_unwarping": False,
    "use_textline_orientation": False,
    "use_formula_recognition": False,
    "use_seal_recognition": False,
    "use_chart_recognition": False,
    "use_table_recognition": True,
    "enable_mkldnn": False,
    "text_detection_model_name": "PP-OCRv5_mobile_det",
    "text_recognition_model_name": "latin_PP-OCRv5_mobile_rec",
}

MODEL_VERSION = "PP-StructureV3 (PP-DocLayout_plus-L, SLANeXt_wired/SLANet_plus, PP-OCRv5_mobile_det + latin_PP-OCRv5_mobile_rec, mkldnn=off)"

# How long the prefetch may take to download ~873 MB before it is abandoned (the feature then stays
# unavailable until the next start).
PREFETCH_TIMEOUT_SECONDS = 1800.0

# Exit code of the child when the kernel killed it; SIGKILL is what the OOM killer sends.
_KILLED = (-signal.SIGKILL, 128 + signal.SIGKILL)


class StructureError(Exception):
    """PP-StructureV3 did not produce a result; the page keeps its PP-OCRv5 blocks."""

    def __init__(self, code: str, detail: str) -> None:
        super().__init__(detail)
        self.code = code


class StructureEngine(Protocol):
    model_version: str

    @property
    def ready(self) -> bool: ...

    def prepare(self) -> None: ...

    def recognize(self, image: Image.Image, *, timeout_seconds: float) -> EngineOutput: ...


class SubprocessStructureEngine:
    """Runs PP-StructureV3 one page at a time in a child process (see the module docstring)."""

    model_version = MODEL_VERSION

    def __init__(self, *, model_cache_dir: str, python: str = sys.executable) -> None:
        self._model_cache_dir = model_cache_dir
        self._python = python
        self._ready = False
        # One child at a time: two would need two budgets of 2.9 GB. A page that finds it busy keeps
        # its PP-OCRv5 blocks instead of waiting on a lock with a deadline running.
        self._busy = threading.Lock()

    @property
    def ready(self) -> bool:
        return self._ready

    def prepare(self) -> None:
        """Downloads the models without loading them. Blocking; call it off the event loop."""
        code = self._run(["prefetch"], timeout_seconds=PREFETCH_TIMEOUT_SECONDS)
        if code != 0:
            raise StructureError("PREFETCH_FAILED", f"model prefetch exited with {code}")
        self._ready = True

    def recognize(self, image: Image.Image, *, timeout_seconds: float) -> EngineOutput:
        if not self._ready:
            raise StructureError("NOT_READY", "PP-StructureV3 models are not downloaded yet")
        if not self._busy.acquire(blocking=False):
            raise StructureError("BUSY", "another page is using PP-StructureV3")

        try:
            with tempfile.TemporaryDirectory(prefix="docreader-structure-") as directory:
                source = Path(directory) / "page.png"
                target = Path(directory) / "result.json"
                image.convert("RGB").save(source, format="PNG", compress_level=1)

                code = self._run(["predict", str(source), str(target)], timeout_seconds=timeout_seconds)
                if code is None:
                    raise StructureError("TIMEOUT", f"PP-StructureV3 did not finish within {timeout_seconds:.0f} s")
                if code in _KILLED:
                    raise StructureError("KILLED", "PP-StructureV3 was killed, most likely out of memory")
                if code != 0 or not target.exists():
                    raise StructureError("FAILED", f"PP-StructureV3 exited with {code}")

                payload = json.loads(target.read_text(encoding="utf-8"))
        finally:
            self._busy.release()

        return EngineOutput(structure_blocks(payload), payload)

    def _run(self, arguments: list[str], *, timeout_seconds: float) -> int | None:
        """Runs the child; its exit code, or None when it was killed at the deadline."""
        environment = dict(os.environ)
        environment.update(
            {
                "FLAGS_use_mkldnn": "false",
                "PADDLE_PDX_CACHE_HOME": self._model_cache_dir,
                "PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK": "True",
            }
        )
        # Paddle's own logging goes nowhere: it is not structured, and the service logs its own line.
        process = subprocess.Popen(
            [self._python, "-m", "app.structure", *arguments],
            cwd=str(Path(__file__).resolve().parent.parent),
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        try:
            return process.wait(timeout=max(1.0, timeout_seconds))
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
            return None


def structure_blocks(payload: dict[str, Any]) -> list[RecognizedBlock]:
    """Blocks of a PP-StructureV3 page: one per table cell inside tables, one per text line outside.

    A table cell becomes one block (its recognized lines joined, its confidence their mean, its box
    the cell's box): what makes the second engine worth its cost is exactly that a cell is a unit. Text
    outside every table comes from the page-wide OCR, line by line, like PP-OCRv5 blocks. Blocks are
    returned top to bottom, left to right.
    """
    blocks: list[tuple[float, float, RecognizedBlock]] = []
    table_regions: list[tuple[float, float, float, float]] = []

    for table in payload.get("table_res_list") or []:
        cells = [_rectangle(cell) for cell in table.get("cell_box_list") or []]
        cells = [cell for cell in cells if cell is not None]
        ocr = table.get("table_ocr_pred") or {}
        lines = _ocr_lines(ocr)
        if cells:
            table_regions.append(
                (min(c[0] for c in cells), min(c[1] for c in cells), max(c[2] for c in cells), max(c[3] for c in cells))
            )

        assigned: set[int] = set()
        for cell in cells:
            members = [
                (index, line) for index, line in enumerate(lines) if index not in assigned and _inside(line[3], cell)
            ]
            if not members:
                continue
            assigned.update(index for index, _ in members)
            members.sort(key=lambda item: (item[1][3][1], item[1][3][0]))
            text = " ".join(line[0] for _, line in members)
            scores = [line[1] for _, line in members if line[1] is not None]
            confidence = round(sum(scores) / len(scores), 4) if scores else None
            left, top, right, bottom = (round(value, 1) for value in cell)
            blocks.append((top, left, RecognizedBlock(text, confidence, [left, top, right, top, right, bottom, left, bottom])))

        for index, line in enumerate(lines):
            if index not in assigned:
                blocks.append((line[3][1], line[3][0], RecognizedBlock(line[0], line[1], line[2])))

    for text, confidence, polygon, centre in _ocr_lines(payload.get("overall_ocr_res") or {}):
        if any(_inside(centre, region) for region in table_regions):
            continue
        blocks.append((centre[1], centre[0], RecognizedBlock(text, confidence, polygon)))

    blocks.sort(key=lambda item: (item[0], item[1]))
    return [block for _, _, block in blocks]


def _ocr_lines(ocr: dict[str, Any]) -> list[tuple[str, float | None, list[float], tuple[float, float]]]:
    """(text, confidence, flat polygon, centre) of each recognized line of a PaddleOCR result."""
    texts = ocr.get("rec_texts") or []
    scores = ocr.get("rec_scores") or []
    polygons = ocr.get("rec_polys") or ocr.get("dt_polys") or []

    lines = []
    for index, text in enumerate(texts):
        if not str(text).strip():
            continue
        polygon = flatten_polygon(polygons[index]) if index < len(polygons) else []
        if len(polygon) < 4:
            continue
        xs, ys = polygon[0::2], polygon[1::2]
        score = round(float(scores[index]), 4) if index < len(scores) else None
        lines.append((str(text), score, polygon, ((min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2)))
    return lines


def _rectangle(box: Sequence[Any]) -> tuple[float, float, float, float] | None:
    try:
        left, top, right, bottom = (float(value) for value in box[:4])
    except (TypeError, ValueError):
        return None
    if right <= left or bottom <= top:
        return None
    return left, top, right, bottom


def _inside(point: tuple[float, float], box: tuple[float, float, float, float]) -> bool:
    return box[0] <= point[0] <= box[2] and box[1] <= point[1] <= box[3]


def _mark_first_to_kill() -> None:
    """Asks the kernel to pick this process first on out-of-memory. Raising it needs no privilege."""
    try:
        Path("/proc/self/oom_score_adj").write_text("1000")
    except OSError:
        pass


def _prefetch() -> int:
    from paddlex.inference.utils.official_models import official_models

    for name in STRUCTURE_MODELS:
        official_models[name]
    return 0


def _predict(source: str, target: str) -> int:
    import numpy as np
    from paddleocr import PPStructureV3

    pipeline = PPStructureV3(**PIPELINE_OPTIONS)
    with Image.open(source) as image:
        bgr = np.asarray(image.convert("RGB"))[:, :, ::-1].copy()

    payload: dict[str, Any] = {}
    for page in pipeline.predict(input=bgr) or []:
        candidate = getattr(page, "json", None)
        if isinstance(candidate, dict):
            payload = candidate.get("res", candidate)
            break

    payload = to_jsonable(payload)
    payload["modelVersion"] = MODEL_VERSION
    partial = Path(target).with_suffix(".partial")
    partial.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
    partial.replace(target)
    return 0


def main(arguments: list[str]) -> int:
    _mark_first_to_kill()
    if arguments[:1] == ["prefetch"]:
        return _prefetch()
    if arguments[:1] == ["predict"] and len(arguments) == 3:
        return _predict(arguments[1], arguments[2])
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
