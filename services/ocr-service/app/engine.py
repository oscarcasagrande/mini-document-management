"""OCR engine behind the service.

The contract is ``OcrEngine``: one RGB image in, blocks and the untouched provider payload out. The
PaddleOCR implementation is the only one that ships, and it is imported lazily so the rest of the
service (and its tests) never needs the vision stack installed.
"""

from __future__ import annotations

import os
import time
from dataclasses import dataclass, field
from typing import Any, Protocol

from PIL import Image


@dataclass(frozen=True)
class RecognizedBlock:
    text: str
    confidence: float | None
    # Flat x/y pairs of the text polygon, in the pixel space of the analysed image.
    bounding_box: list[float] = field(default_factory=list)


@dataclass(frozen=True)
class EngineOutput:
    blocks: list[RecognizedBlock]
    raw: dict[str, Any]


class OcrEngine(Protocol):
    model_version: str

    def load(self) -> None: ...

    def recognize(self, image: Image.Image) -> EngineOutput: ...


def to_jsonable(value: Any) -> Any:
    """Recursively turns numpy scalars and arrays into plain Python so the payload serializes."""
    if isinstance(value, dict):
        return {str(key): to_jsonable(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [to_jsonable(item) for item in value]
    if hasattr(value, "tolist"):
        return to_jsonable(value.tolist())
    if isinstance(value, float):
        return round(value, 6)
    if isinstance(value, (str, int, bool)) or value is None:
        return value

    return str(value)


def flatten_polygon(polygon: Any) -> list[float]:
    """[[x1, y1], [x2, y2], ...] to [x1, y1, x2, y2, ...], rounded to a tenth of a pixel."""
    points = to_jsonable(polygon) or []
    flat: list[float] = []
    for point in points:
        if isinstance(point, list) and len(point) == 2:
            flat.extend(round(float(coordinate), 1) for coordinate in point)

    return flat


@dataclass(frozen=True)
class ModelProfile:
    """A detector and recognizer pair, selected by name with OCR_MODEL_PROFILE (docs/adr/0002)."""

    name: str
    ocr_version: str
    detection_model: str
    # None lets PaddleOCR pick the recognizer for lang="pt"; the v5 profile relies on that to get the
    # Latin mobile recognizer.
    recognition_model: str | None
    label: str


PROFILES: dict[str, ModelProfile] = {
    profile.name: profile
    for profile in (
        ModelProfile("ppocrv5-mobile", "PP-OCRv5", "PP-OCRv5_mobile_det", None, "PP-OCRv5"),
        ModelProfile("ppocrv6-medium", "PP-OCRv6", "PP-OCRv6_medium_det", "PP-OCRv6_medium_rec", "PP-OCRv6"),
        ModelProfile("ppocrv6-small", "PP-OCRv6", "PP-OCRv6_small_det", "PP-OCRv6_small_rec", "PP-OCRv6"),
        ModelProfile("ppocrv6-tiny", "PP-OCRv6", "PP-OCRv6_tiny_det", "PP-OCRv6_tiny_rec", "PP-OCRv6"),
    )
}

DEFAULT_PROFILE = "ppocrv5-mobile"


def resolve_profile(name: str) -> ModelProfile:
    """Looks a profile up by name; an unknown name fails at startup instead of on the first page."""
    try:
        return PROFILES[name]
    except KeyError:
        raise ValueError(f"unknown OCR_MODEL_PROFILE '{name}'; choose one of: {', '.join(sorted(PROFILES))}") from None


class PaddleOcrEngine:
    """PaddleOCR with the detector and recognizer of the configured profile.

    The default profile is the pipeline approved in ADR 0002. Every profile runs with oneDNN on unless
    OCR_ENABLE_MKLDNN=false.
    """

    def __init__(self, *, enable_mkldnn: bool, model_cache_dir: str, profile: str = DEFAULT_PROFILE) -> None:
        self._enable_mkldnn = enable_mkldnn
        self._model_cache_dir = model_cache_dir
        self._profile = resolve_profile(profile)
        self._engine: Any = None
        self.model_version = f"{self._profile.label} (not loaded)"
        self.warmup_ms = 0.0

    def load(self) -> None:
        # These must be set before paddle is imported: the flag takes precedence over the
        # enable_mkldnn parameter, and the cache directory is read at import time.
        os.environ["PADDLE_PDX_CACHE_HOME"] = self._model_cache_dir
        os.environ["FLAGS_use_mkldnn"] = "true" if self._enable_mkldnn else "false"
        os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")

        from importlib import metadata

        from paddleocr import PaddleOCR

        profile = self._profile
        options: dict[str, Any] = {
            "use_doc_orientation_classify": False,
            "use_doc_unwarping": False,
            "use_textline_orientation": False,
            "lang": "pt",
            "ocr_version": profile.ocr_version,
            "text_detection_model_name": profile.detection_model,
            "enable_mkldnn": self._enable_mkldnn,
        }
        if profile.recognition_model:
            options["text_recognition_model_name"] = profile.recognition_model

        self._engine = PaddleOCR(**options)

        recognizer = profile.recognition_model or "latin_PP-OCRv5_mobile_rec"
        self.model_version = (
            f"{profile.label} {profile.detection_model} + {recognizer} "
            f"(paddleocr {metadata.version('paddleocr')}, paddlepaddle {metadata.version('paddlepaddle')}, "
            f"mkldnn={'on' if self._enable_mkldnn else 'off'})"
        )

        # The first inference pays cache and buffer allocation. Paying it here keeps the first real
        # page as fast as the following ones.
        started = time.perf_counter()
        self.recognize(Image.new("RGB", (320, 96), (255, 255, 255)))
        self.warmup_ms = (time.perf_counter() - started) * 1000

    def recognize(self, image: Image.Image) -> EngineOutput:
        import numpy as np

        # PaddleOCR reads BGR, as OpenCV does when it opens a file.
        bgr = np.asarray(image.convert("RGB"))[:, :, ::-1].copy()
        results = self._engine.predict(input=bgr)

        payload: dict[str, Any] = {}
        for page in results or []:
            candidate = getattr(page, "json", None)
            if isinstance(candidate, dict):
                payload = candidate.get("res", candidate)
                break

        texts = payload.get("rec_texts") or []
        scores = payload.get("rec_scores") or []
        polygons = payload.get("rec_polys")
        if polygons is None:
            polygons = payload.get("dt_polys") or []

        blocks: list[RecognizedBlock] = []
        for index, text in enumerate(texts):
            confidence = float(scores[index]) if index < len(scores) else None
            polygon = flatten_polygon(polygons[index]) if index < len(polygons) else []
            blocks.append(RecognizedBlock(str(text), None if confidence is None else round(confidence, 4), polygon))

        return EngineOutput(blocks, to_jsonable(payload))
