"""Local OCR service.

Stage 1 of the execution plan ships this service as a stub: it is part of the compose topology and it
answers health probes so the API readiness check has something real to talk to, but it does not load
PaddleOCR yet. Stage 2 replaces ``analyze`` with the PP-StructureV3 pipeline.
"""

from __future__ import annotations

import logging
import os
import sys
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from datetime import datetime, timezone

from fastapi import FastAPI, status
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

SERVICE_NAME = "ocr-service"
STAGE = 1
PROVIDER = os.getenv("OCR_PROVIDER", "paddleocr")
MODEL_CACHE_DIR = os.getenv("OCR_MODEL_CACHE_DIR", "/var/lib/docreader/ocr-models")

logging.basicConfig(
    stream=sys.stdout,
    level=os.getenv("OCR_LOG_LEVEL", "INFO"),
    format='{"timestamp":"%(asctime)s","level":"%(levelname)s","logger":"%(name)s","message":"%(message)s"}',
)
logger = logging.getLogger(SERVICE_NAME)

@asynccontextmanager
async def lifespan(_: FastAPI) -> AsyncIterator[None]:
    logger.info(
        "ocr-service started as a stage 1 stub. provider=%s model_cache_dir=%s",
        PROVIDER,
        MODEL_CACHE_DIR,
    )
    yield
    logger.info("ocr-service stopping")


app = FastAPI(
    title="DocReader OCR service",
    version="0.1.0",
    description=(
        "Internal OCR service of the DocReader proof of concept. Not exposed outside the Docker "
        "network. In stage 1 it only answers health probes."
    ),
    lifespan=lifespan,
)


class HealthResponse(BaseModel):
    """Answer of the health probes."""

    status: str = Field(examples=["healthy"])
    service: str = Field(examples=[SERVICE_NAME])
    stage: int = Field(examples=[STAGE])
    role: str = Field(examples=["stub"])
    provider: str = Field(examples=["paddleocr"])
    model_loaded: bool = Field(examples=[False])
    checked_at: datetime


def _health_payload() -> HealthResponse:
    return HealthResponse(
        status="healthy",
        service=SERVICE_NAME,
        stage=STAGE,
        role="stub",
        provider=PROVIDER,
        model_loaded=False,
        checked_at=datetime.now(tz=timezone.utc),
    )


@app.get("/health", response_model=HealthResponse, tags=["health"])
def health() -> HealthResponse:
    """Liveness and readiness of the OCR service."""
    return _health_payload()


@app.get("/health/live", response_model=HealthResponse, tags=["health"])
def health_live() -> HealthResponse:
    """Liveness probe, kept separate so it mirrors the API contract."""
    return _health_payload()


@app.get("/health/ready", response_model=HealthResponse, tags=["health"])
def health_ready() -> HealthResponse:
    """Readiness probe. In stage 1 readiness does not depend on a loaded model."""
    return _health_payload()


@app.post("/analyze", tags=["ocr"])
def analyze() -> JSONResponse:
    """Reserved for stage 2. Answers 501 so a premature caller fails loudly."""
    logger.warning("analyze called while the service is still a stage 1 stub")
    return JSONResponse(
        status_code=status.HTTP_501_NOT_IMPLEMENTED,
        content={
            "type": "https://docreader.local/problems/ocr-not-implemented",
            "title": "OCR is not implemented yet",
            "status": status.HTTP_501_NOT_IMPLEMENTED,
            "detail": (
                "This service is a stage 1 stub. PaddleOCR with PP-StructureV3 arrives in stage 2 of "
                "the execution plan."
            ),
        },
        media_type="application/problem+json",
    )
