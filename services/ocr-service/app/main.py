"""Local OCR service of the DocReader proof of concept.

PaddleOCR on CPU (PP-OCRv5 mobile by default, see OCR_MODEL_PROFILE and ADR 0002), one page per
call. The worker sends the original file and a page number; this service rasterizes that page when
needed, reads it and answers blocks with confidence and coordinates, plus the untouched provider
payload. It is reachable only from the internal Docker network.

Nothing here logs document content: only ids, sizes, counts and durations.
"""

from __future__ import annotations

import asyncio
import json
import logging
import re
import sys
import time
import uuid
from collections.abc import AsyncIterator, Callable
from concurrent.futures import ThreadPoolExecutor
from contextlib import asynccontextmanager
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, TypeVar

from fastapi import FastAPI, File, Form, Request, UploadFile
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel

from .engine import OcrEngine, PaddleOcrEngine, RecognizedBlock
from .native_text import read_native_page
from .pages import (
    PageOutOfRangeError,
    UnreadableFileError,
    UnsupportedFormatError,
    detect_kind,
    limit_side,
    render_page,
)
from .preprocess import correct_orientation
from .settings import Settings
from .structure import StructureEngine, StructureError, SubprocessStructureEngine
from .tables import assess_table

# Page budget kept back when deciding whether PP-StructureV3 still fits before the page deadline, and
# the least it must be left with to be worth starting (load alone is ~6 s, a page 27-40 s).
STRUCTURE_DEADLINE_MARGIN_SECONDS = 3.0
STRUCTURE_MIN_BUDGET_SECONDS = 20.0

# Memory the container should have with PP-StructureV3 on (ADR 0002, addendum of 2026-09-29).
STRUCTURE_RECOMMENDED_MEMORY_BYTES = 6 * 1024**3


def container_memory_limit() -> int | None:
    """The cgroup memory ceiling of this container, or None when there is none or it cannot be read."""
    for path in ("/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory/memory.limit_in_bytes"):
        try:
            raw = Path(path).read_text().strip()
        except OSError:
            continue
        if raw == "max":
            return None
        try:
            value = int(raw)
        except ValueError:
            return None
        # cgroup v1 reports "no limit" as a huge number rather than "max".
        return value if value < 1 << 60 else None
    return None

SERVICE_NAME = "ocr-service"
CORRELATION_HEADER = "X-Correlation-Id"
PROBLEM_MEDIA_TYPE = "application/problem+json"
PROBLEM_BASE = "https://docreader.local/problems/"

# The same rule the API applies: a client value is accepted only when it is short and boring.
_CORRELATION_PATTERN = re.compile(r"^[A-Za-z0-9._\-]{8,64}$")

T = TypeVar("T")


class JsonFormatter(logging.Formatter):
    """One JSON object per line, in the same spirit as the .NET services."""

    def format(self, record: logging.LogRecord) -> str:
        entry: dict[str, Any] = {
            "timestamp": datetime.fromtimestamp(record.created, tz=timezone.utc).isoformat(timespec="milliseconds"),
            "level": record.levelname,
            "logger": record.name,
            "message": record.getMessage(),
        }
        correlation_id = getattr(record, "correlation_id", None)
        if correlation_id:
            entry["correlationId"] = correlation_id
        if record.exc_info:
            # The type and the frame are enough to debug; the message could quote document bytes.
            entry["exception"] = record.exc_info[0].__name__ if record.exc_info[0] else None

        return json.dumps(entry, ensure_ascii=False)


def configure_logging(level: str) -> logging.Logger:
    handler = logging.StreamHandler(sys.stdout)
    handler.setFormatter(JsonFormatter())

    root = logging.getLogger()
    root.handlers = [handler]
    root.setLevel(level)

    return logging.getLogger(SERVICE_NAME)


class ApiModel(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)


class HealthResponse(ApiModel):
    """Answer of the health probes."""

    status: str = Field(examples=["healthy"])
    service: str = Field(examples=[SERVICE_NAME])
    provider: str = Field(examples=["paddleocr"])
    model_loaded: bool = Field(examples=[True])
    model_version: str | None = Field(default=None)
    model_load_ms: float | None = Field(default=None)
    max_concurrency: int = Field(examples=[1])
    structure_enabled: bool = Field(
        default=False, description="OCR_USE_PP_STRUCTUREV3_FOR_TABLES: PP-StructureV3 may re-read low-confidence tables."
    )
    structure_ready: bool = Field(
        default=False, description="PP-StructureV3 models are downloaded; until then no page is sent to it."
    )
    checked_at: datetime


class OcrBlockResponse(ApiModel):
    text: str
    confidence: float | None = Field(description="Recognition confidence between 0 and 1.")
    bounding_box: list[float] = Field(description="Flat x/y pairs of the text polygon, in analysed image pixels.")


class PageAnalysisResponse(ApiModel):
    """One analysed page."""

    page: int = Field(description="One based page number that was analysed.")
    page_count: int = Field(description="Pages in the file, so the caller can validate its own count.")
    image_width: int = Field(description="Width of the analysed image; coordinates are in this space.")
    image_height: int
    provider: str
    model_version: str
    duration_ms: int = Field(description="Rasterization plus recognition, waiting for a slot excluded.")
    blocks: list[OcrBlockResponse]
    raw: dict[str, Any] = Field(
        description=(
            "Untouched provider payload, preserved as required by RF-008: the PP-OCRv5 result; "
            '{"source": "pdfplumber", ...} for a page read from its text layer; '
            '{"v5": ..., "structureV3": ...} for a page PP-StructureV3 re-read.'
        )
    )
    has_native_text_layer: bool = Field(
        default=False, description="The page was read from the PDF text layer; no rasterization and no OCR (RF-009)."
    )
    rotation_degrees: int = Field(
        default=0, description="Clockwise cardinal correction applied before OCR: 0, 90, 180 or 270."
    )
    deskewed: bool = Field(default=False, description="A small tilt was also straightened before OCR.")
    processed_with_structure: bool = Field(
        default=False, description="Blocks come from PP-StructureV3, which re-read a low-confidence table."
    )


@dataclass(frozen=True)
class PageOutcome:
    """What the blocking part of a request produced, before it becomes the response."""

    page_count: int
    width: int
    height: int
    blocks: list[RecognizedBlock]
    raw: dict[str, Any]
    elapsed_ms: int
    has_native_text_layer: bool = False
    rotation_degrees: int = 0
    deskewed: bool = False
    processed_with_structure: bool = False
    # Why PP-StructureV3 was considered and not used, for the log; None when it was not considered.
    structure_skipped: str | None = None


class ServiceError(Exception):
    """An error that maps to a problem+json answer."""

    def __init__(self, status: int, code: str, title: str, detail: str, retry_after: int | None = None) -> None:
        super().__init__(detail)
        self.status = status
        self.code = code
        self.title = title
        self.detail = detail
        self.retry_after = retry_after


def _problem(request: Request, error: ServiceError) -> JSONResponse:
    body = {
        "type": f"{PROBLEM_BASE}{error.code.lower().replace('_', '-')}",
        "title": error.title,
        "status": error.status,
        "detail": error.detail,
        "instance": request.url.path,
        "errorCode": error.code,
        "correlationId": getattr(request.state, "correlation_id", None),
    }
    headers = {"Retry-After": str(error.retry_after)} if error.retry_after else None

    return JSONResponse(status_code=error.status, content=body, media_type=PROBLEM_MEDIA_TYPE, headers=headers)


class InferenceLimiter:
    """Runs blocking work under a fixed number of slots and a wall clock budget.

    A timed out call is reported to the caller, but its slot is only released when the thread really
    finishes: PaddleOCR cannot be interrupted, and releasing early would let two inferences run at
    once and break the memory budget the service was sized for.
    """

    def __init__(self, slots: int, timeout_seconds: float) -> None:
        self._slots = asyncio.Semaphore(slots)
        self._executor = ThreadPoolExecutor(max_workers=slots, thread_name_prefix="ocr")
        self._timeout = timeout_seconds

    async def run(self, work: Callable[[], T]) -> T:
        try:
            await asyncio.wait_for(self._slots.acquire(), timeout=self._timeout)
        except asyncio.TimeoutError as timeout:
            raise ServiceError(
                503,
                "OCR_BUSY",
                "OCR service is busy",
                "No inference slot became free in time. Retry shortly.",
                retry_after=5,
            ) from timeout

        loop = asyncio.get_running_loop()
        future = loop.run_in_executor(self._executor, work)

        def release(finished: asyncio.Future[T]) -> None:
            if not finished.cancelled():
                finished.exception()  # marks it retrieved when nobody is awaiting it any more
            self._slots.release()

        future.add_done_callback(release)

        try:
            return await asyncio.wait_for(asyncio.shield(future), timeout=self._timeout)
        except asyncio.TimeoutError as timeout:
            raise ServiceError(
                504,
                "OCR_PAGE_TIMEOUT",
                "OCR page timed out",
                f"The page did not finish within {self._timeout:g} seconds.",
            ) from timeout

    def shutdown(self) -> None:
        self._executor.shutdown(wait=False, cancel_futures=True)


class ServiceState:
    """What the health probes report."""

    def __init__(self) -> None:
        self.model_loaded = False
        self.model_load_ms: float | None = None
        self.load_error: str | None = None


def create_app(
    engine: OcrEngine | None = None,
    settings: Settings | None = None,
    structure_engine: StructureEngine | None = None,
) -> FastAPI:
    settings = settings or Settings.from_environment()
    logger = configure_logging(settings.log_level)
    state = ServiceState()
    limiter = InferenceLimiter(settings.max_concurrency, settings.page_timeout_seconds)
    active_engine: OcrEngine = engine or PaddleOcrEngine(
        enable_mkldnn=settings.enable_mkldnn,
        model_cache_dir=settings.model_cache_dir,
        profile=settings.model_profile,
    )
    structure: StructureEngine | None = None
    if settings.use_structure_for_tables:
        structure = structure_engine or SubprocessStructureEngine(model_cache_dir=settings.model_cache_dir)

    async def prepare_structure() -> None:
        if structure is None:
            return
        limit = container_memory_limit()
        if limit is not None and limit < STRUCTURE_RECOMMENDED_MEMORY_BYTES:
            # Not fatal: the child is the first out-of-memory victim and pages fall back to PP-OCRv5.
            logger.warning(
                "PP-StructureV3 enabled under a small memory limit; table pages will likely fall back to "
                "PP-OCRv5. memoryLimitBytes=%s recommendedBytes=%s",
                limit,
                STRUCTURE_RECOMMENDED_MEMORY_BYTES,
            )
        started = time.perf_counter()
        try:
            await asyncio.to_thread(structure.prepare)
        except Exception as exception:  # noqa: BLE001 - the feature stays off, the service does not
            logger.warning(
                "PP-StructureV3 unavailable, tables stay with PP-OCRv5. error=%s code=%s",
                type(exception).__name__,
                getattr(exception, "code", None),
            )
            return
        logger.info("PP-StructureV3 models ready. prepareMs=%s", round((time.perf_counter() - started) * 1000))

    async def load_model() -> None:
        started = time.perf_counter()
        try:
            await asyncio.to_thread(active_engine.load)
        except Exception as exception:  # noqa: BLE001 - reported through the health probe
            state.load_error = type(exception).__name__
            logger.error("model load failed. error=%s", state.load_error)
            return

        state.model_load_ms = round((time.perf_counter() - started) * 1000, 1)
        state.model_loaded = True
        logger.info(
            "model loaded. provider=%s modelVersion=%s loadMs=%s maxConcurrency=%s pageTimeoutSeconds=%s",
            settings.provider,
            active_engine.model_version,
            state.model_load_ms,
            settings.max_concurrency,
            settings.page_timeout_seconds,
        )

    @asynccontextmanager
    async def lifespan(_: FastAPI) -> AsyncIterator[None]:
        logger.info(
            "ocr-service starting. provider=%s profile=%s orientationCorrection=%s structureForTables=%s",
            settings.provider,
            settings.model_profile,
            settings.orientation_correction,
            settings.use_structure_for_tables,
        )
        # Loading in the background keeps /health/live answering while the models come up. The
        # PP-StructureV3 download does not gate readiness: until it ends, tables stay with PP-OCRv5.
        loading = asyncio.create_task(load_model())
        preparing = asyncio.create_task(prepare_structure())
        yield
        loading.cancel()
        preparing.cancel()
        limiter.shutdown()
        logger.info("ocr-service stopping")

    app = FastAPI(
        title="DocReader OCR service",
        version="0.3.0",
        description=(
            "Internal OCR service of the DocReader proof of concept. Not exposed outside the Docker "
            "network. Reads one page per call with PP-OCRv5 on CPU; a PDF page with a real text layer "
            "is read from it instead, other pages are turned upright and deskewed first, and a "
            "low-confidence table may be re-read by PP-StructureV3 (OCR_USE_PP_STRUCTUREV3_FOR_TABLES)."
        ),
        lifespan=lifespan,
    )

    @app.middleware("http")
    async def correlation(request: Request, call_next: Callable[[Request], Any]) -> Any:
        supplied = request.headers.get(CORRELATION_HEADER, "")
        correlation_id = supplied if _CORRELATION_PATTERN.match(supplied) else uuid.uuid4().hex
        request.state.correlation_id = correlation_id

        response = await call_next(request)
        response.headers[CORRELATION_HEADER] = correlation_id
        return response

    @app.exception_handler(ServiceError)
    async def handle_service_error(request: Request, error: ServiceError) -> JSONResponse:
        return _problem(request, error)

    @app.exception_handler(Exception)
    async def handle_unexpected(request: Request, error: Exception) -> JSONResponse:
        logger.error(
            "unhandled error. error=%s",
            type(error).__name__,
            extra={"correlation_id": getattr(request.state, "correlation_id", None)},
        )
        return _problem(
            request,
            ServiceError(500, "OCR_INTERNAL_ERROR", "OCR failed", "The OCR service failed to process the page."),
        )

    def health_payload() -> HealthResponse:
        return HealthResponse(
            status="healthy" if state.model_loaded else "starting",
            service=SERVICE_NAME,
            provider=settings.provider,
            model_loaded=state.model_loaded,
            model_version=active_engine.model_version if state.model_loaded else None,
            model_load_ms=state.model_load_ms,
            max_concurrency=settings.max_concurrency,
            structure_enabled=structure is not None,
            structure_ready=structure is not None and structure.ready,
            checked_at=datetime.now(tz=timezone.utc),
        )

    def readiness() -> JSONResponse:
        payload = health_payload()
        if state.load_error is not None:
            payload.status = "unhealthy"

        code = 200 if state.model_loaded else 503
        return JSONResponse(status_code=code, content=json.loads(payload.model_dump_json(by_alias=True)))

    @app.get("/health", response_model=HealthResponse, tags=["health"])
    def health() -> JSONResponse:
        """Readiness: healthy only once the models are loaded and warm."""
        return readiness()

    @app.get("/health/live", response_model=HealthResponse, tags=["health"])
    def health_live() -> HealthResponse:
        """Liveness: the process is up, models may still be loading."""
        return health_payload()

    @app.get("/health/ready", response_model=HealthResponse, tags=["health"])
    def health_ready() -> JSONResponse:
        """Readiness, identical to /health."""
        return readiness()

    @app.post(
        "/v1/ocr/page",
        response_model=PageAnalysisResponse,
        tags=["ocr"],
        responses={
            413: {"description": "The file is above the configured limit."},
            415: {"description": "Not a PDF, PNG, JPEG or TIFF."},
            422: {"description": "The file cannot be decoded or the page does not exist."},
            503: {"description": "Models not loaded yet, or no inference slot free."},
            504: {"description": "The page did not finish within the page timeout."},
        },
    )
    async def analyze_page(
        request: Request,
        file: UploadFile = File(description="The original document, exactly as stored."),
        page: int = Form(1, ge=1, description="One based page to analyse."),
    ) -> PageAnalysisResponse:
        """Reads one page of the file and returns its text blocks."""
        correlation_id = request.state.correlation_id

        if not state.model_loaded:
            raise ServiceError(
                503,
                "OCR_MODEL_NOT_READY",
                "OCR models are not loaded",
                "The service is still loading its models. Retry shortly.",
                retry_after=5,
            )

        content = await file.read()
        if len(content) > settings.max_upload_bytes:
            raise ServiceError(
                413, "FILE_TOO_LARGE", "File too large", f"The file is above {settings.max_upload_bytes} bytes."
            )

        try:
            kind = detect_kind(content)
        except UnsupportedFormatError as error:
            raise ServiceError(415, "UNSUPPORTED_FORMAT", "Unsupported format", str(error)) from error

        def work() -> PageOutcome:
            started = time.perf_counter()

            def elapsed() -> int:
                return round((time.perf_counter() - started) * 1000)

            if kind == "pdf":
                native = read_native_page(
                    content,
                    page,
                    dpi=settings.pdf_dpi,
                    max_side=settings.max_image_side,
                    min_chars=settings.pdf_native_text_min_chars,
                )
                if native is not None:
                    return PageOutcome(
                        native.page_count, native.width, native.height, native.blocks, native.raw, elapsed(),
                        has_native_text_layer=True,
                    )

            rendered = render_page(content, page, pdf_dpi=settings.pdf_dpi, max_side=settings.max_image_side)
            image = rendered.image
            rotation, deskewed = 0, False
            if settings.orientation_correction:
                oriented = correct_orientation(
                    image,
                    deskew_min_degrees=settings.deskew_min_degrees,
                    deskew_max_degrees=settings.deskew_max_degrees,
                )
                # Turning a landscape page or growing the canvas to deskew can pass the side limit.
                image = limit_side(oriented.image, settings.max_image_side)
                rotation, deskewed = oriented.rotation_degrees, oriented.deskewed

            output = active_engine.recognize(image)
            blocks, raw = output.blocks, output.raw
            with_structure, skipped = False, None

            if structure is not None:
                assessment = assess_table(blocks)
                if assessment.triggers(settings.structure_table_confidence_threshold):
                    megapixels = image.width * image.height / 1_000_000
                    budget = settings.page_timeout_seconds - (time.perf_counter() - started) - STRUCTURE_DEADLINE_MARGIN_SECONDS
                    if megapixels > settings.structure_max_megapixels:
                        skipped = f"IMAGE_TOO_LARGE megapixels={megapixels:.2f} max={settings.structure_max_megapixels:g}"
                    elif budget < STRUCTURE_MIN_BUDGET_SECONDS:
                        skipped = f"NO_TIME_LEFT budgetSeconds={budget:.0f}"
                    else:
                        try:
                            structured = structure.recognize(image, timeout_seconds=budget)
                        except StructureError as error:
                            skipped = error.code
                        except Exception as error:  # noqa: BLE001 - never lose a page PP-OCRv5 already read
                            skipped = f"UNEXPECTED_ERROR error={type(error).__name__}"
                        else:
                            blocks = structured.blocks
                            raw = {"v5": output.raw, "structureV3": structured.raw}
                            with_structure = True

                    if skipped is not None:
                        logger.warning(
                            "table page kept with PP-OCRv5. reason=%s gridRows=%s columns=%s gridConfidence=%s",
                            skipped,
                            assessment.grid_rows,
                            assessment.columns,
                            assessment.grid_confidence,
                            extra={"correlation_id": correlation_id},
                        )

            return PageOutcome(
                rendered.page_count, image.width, image.height, blocks, raw, elapsed(),
                rotation_degrees=rotation, deskewed=deskewed, processed_with_structure=with_structure,
                structure_skipped=skipped,
            )

        try:
            outcome = await limiter.run(work)
        except PageOutOfRangeError as error:
            raise ServiceError(422, "PAGE_OUT_OF_RANGE", "Page out of range", str(error)) from error
        except UnreadableFileError as error:
            raise ServiceError(422, "UNREADABLE_FILE", "Unreadable file", str(error)) from error

        logger.info(
            "page analyzed. kind=%s page=%s pageCount=%s width=%s height=%s blocks=%s durationMs=%s "
            "nativeTextLayer=%s rotationDegrees=%s deskewed=%s processedWithStructure=%s",
            kind,
            page,
            outcome.page_count,
            outcome.width,
            outcome.height,
            len(outcome.blocks),
            outcome.elapsed_ms,
            outcome.has_native_text_layer,
            outcome.rotation_degrees,
            outcome.deskewed,
            outcome.processed_with_structure,
            extra={"correlation_id": correlation_id},
        )

        return PageAnalysisResponse(
            page=page,
            page_count=outcome.page_count,
            image_width=outcome.width,
            image_height=outcome.height,
            # The engine's identity even for a page read from its text layer: the .NET side records one
            # provider and model per attempt (the last page's), and a mixed PDF would otherwise be
            # labelled by whichever kind of page came last. The native page says what read it in
            # raw.source ("pdfplumber") and in hasNativeTextLayer.
            provider=settings.provider,
            model_version=active_engine.model_version,
            duration_ms=outcome.elapsed_ms,
            blocks=[
                OcrBlockResponse(text=block.text, confidence=block.confidence, bounding_box=block.bounding_box)
                for block in outcome.blocks
            ],
            raw=outcome.raw,
            has_native_text_layer=outcome.has_native_text_layer,
            rotation_degrees=outcome.rotation_degrees,
            deskewed=outcome.deskewed,
            processed_with_structure=outcome.processed_with_structure,
        )

    return app


app = create_app()
