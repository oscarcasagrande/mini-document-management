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
        )
