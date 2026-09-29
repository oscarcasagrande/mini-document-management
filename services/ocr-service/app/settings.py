"""Configuration of the OCR service, read once from the environment.

Every knob is an ``OCR_*`` variable so the compose file is the single place that tunes the service.
"""

from __future__ import annotations

import os
from dataclasses import dataclass


def _env_int(name: str, default: int) -> int:
    raw = os.getenv(name)
    if raw is None or raw.strip() == "":
        return default
    return int(raw)


def _env_float(name: str, default: float) -> float:
    raw = os.getenv(name)
    if raw is None or raw.strip() == "":
        return default
    return float(raw)


def _env_bool(name: str, default: bool) -> bool:
    raw = os.getenv(name)
    if raw is None or raw.strip() == "":
        return default
    return raw.strip().lower() in {"1", "true", "yes", "on"}


@dataclass(frozen=True)
class Settings:
    provider: str = "paddleocr"

    # Which detector and recognizer to load: ppocrv5-mobile, ppocrv6-medium, ppocrv6-small or
    # ppocrv6-tiny (docs/adr/0002). The models of the default are baked into the image.
    model_profile: str = "ppocrv5-mobile"

    model_cache_dir: str = "/var/lib/docreader/ocr-models"
    log_level: str = "INFO"

    # Inference slots. One page at a time is what the 3 GB memory budget was sized for; a request that
    # cannot get a slot within ``page_timeout_seconds`` is answered with 503 so the worker retries.
    max_concurrency: int = 1

    # Wall clock budget of one page, waiting for a slot included. Inference itself cannot be
    # interrupted, so a timed out page keeps its slot until the thread ends: the limit on concurrent
    # inference holds even when a caller gave up.
    page_timeout_seconds: float = 120.0

    # Resolution used to rasterize PDF pages. 200 DPI is what the benchmark measured.
    pdf_dpi: int = 200

    # Larger images are downscaled before detection. A4 at 200 DPI (2339 px) is below the limit.
    max_image_side: int = 3200

    max_upload_bytes: int = 26_214_400

    # oneDNN on: 23-34% faster with identical output on paddlepaddle 3.2.2 (docs/adr/0002). Turn it off
    # (OCR_ENABLE_MKLDNN=false) if a different paddlepaddle build is installed: 3.3.1 fails every
    # inference with it.
    enable_mkldnn: bool = True

    # RF-009, native text layer: a PDF page whose embedded text has at least this many letters and
    # digits is read from the PDF itself and never rasterized. Below it the page goes to OCR, so an
    # empty layer, a lone page number or a "(cid:12)" garbage layer is not mistaken for the content.
    pdf_native_text_min_chars: int = 20

    # RF-009, orientation: turn a sideways or upside-down page upright (0/90/180/270) and straighten a
    # small tilt before OCR. The deskew leaves tilts below the floor alone (not worth an interpolation)
    # and treats those above the cap as a detection error rather than a page fed that crooked.
    orientation_correction: bool = True
    deskew_min_degrees: float = 0.5
    deskew_max_degrees: float = 20.0

    # RF-009, PP-StructureV3 on demand. Off by default. When on, a page whose PP-OCRv5 blocks form a
    # grid (docs in app/tables.py) with mean confidence below the threshold is read again by
    # PP-StructureV3 in a child process, if the image is within the megapixel ceiling. 4.0 MP covers an
    # A4 page at 200 DPI (3.87 MP), measured at 2.84 GB peak for the child alone; the service needs
    # OCR_MEMORY_LIMIT=6g with this on (see .env.example).
    use_structure_for_tables: bool = False
    structure_max_megapixels: float = 4.0
    structure_table_confidence_threshold: float = 0.80

    @staticmethod
    def from_environment() -> "Settings":
        defaults = Settings()

        return Settings(
            provider=os.getenv("OCR_PROVIDER", defaults.provider),
            model_profile=os.getenv("OCR_MODEL_PROFILE", defaults.model_profile).strip().lower(),
            model_cache_dir=os.getenv("OCR_MODEL_CACHE_DIR", defaults.model_cache_dir),
            log_level=os.getenv("OCR_LOG_LEVEL", defaults.log_level).upper(),
            max_concurrency=max(1, _env_int("OCR_MAX_CONCURRENCY", defaults.max_concurrency)),
            page_timeout_seconds=_env_float("OCR_PAGE_TIMEOUT_SECONDS", defaults.page_timeout_seconds),
            pdf_dpi=_env_int("OCR_PDF_DPI", defaults.pdf_dpi),
            max_image_side=_env_int("OCR_MAX_IMAGE_SIDE", defaults.max_image_side),
            max_upload_bytes=_env_int("OCR_MAX_UPLOAD_BYTES", defaults.max_upload_bytes),
            enable_mkldnn=_env_bool("OCR_ENABLE_MKLDNN", defaults.enable_mkldnn),
            pdf_native_text_min_chars=max(
                1, _env_int("OCR_PDF_NATIVE_TEXT_MIN_CHARS", defaults.pdf_native_text_min_chars)
            ),
            orientation_correction=_env_bool("OCR_ORIENTATION_CORRECTION", defaults.orientation_correction),
            deskew_min_degrees=_env_float("OCR_DESKEW_MIN_DEGREES", defaults.deskew_min_degrees),
            deskew_max_degrees=_env_float("OCR_DESKEW_MAX_DEGREES", defaults.deskew_max_degrees),
            use_structure_for_tables=_env_bool(
                "OCR_USE_PP_STRUCTUREV3_FOR_TABLES", defaults.use_structure_for_tables
            ),
            structure_max_megapixels=_env_float("OCR_STRUCTURE_MAX_MEGAPIXELS", defaults.structure_max_megapixels),
            structure_table_confidence_threshold=_env_float(
                "OCR_STRUCTURE_TABLE_CONFIDENCE_THRESHOLD", defaults.structure_table_confidence_threshold
            ),
        )
