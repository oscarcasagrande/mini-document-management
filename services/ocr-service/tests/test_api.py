import io
import threading
import time

import pytest
from fastapi.testclient import TestClient
from PIL import Image

from app.engine import EngineOutput, RecognizedBlock
from app.main import create_app
from app.settings import Settings


class FakeEngine:
    """Stands in for PaddleOCR: deterministic and free of the vision stack."""

    model_version = "fake-engine 1.0"

    def __init__(self, delay: float = 0.0, load_delay: float = 0.0) -> None:
        self.delay = delay
        self.load_delay = load_delay
        self.active = 0
        self.peak_active = 0
        self._lock = threading.Lock()

    def load(self) -> None:
        time.sleep(self.load_delay)

    def recognize(self, image: Image.Image) -> EngineOutput:
        with self._lock:
            self.active += 1
            self.peak_active = max(self.peak_active, self.active)

        try:
            time.sleep(self.delay)
        finally:
            with self._lock:
                self.active -= 1

        return EngineOutput(
            [RecognizedBlock("111.444.777-35", 0.999, [1.0, 2.0, 30.0, 2.0, 30.0, 12.0, 1.0, 12.0])],
            {"rec_texts": ["111.444.777-35"]},
        )


def png() -> bytes:
    buffer = io.BytesIO()
    Image.new("RGB", (200, 100), (255, 255, 255)).save(buffer, format="PNG")
    return buffer.getvalue()


def make_client(engine: FakeEngine | None = None, **settings) -> tuple[TestClient, FakeEngine]:
    engine = engine or FakeEngine()
    app = create_app(engine=engine, settings=Settings(log_level="WARNING", **settings))
    return TestClient(app), engine


def wait_until_ready(client: TestClient) -> None:
    for _ in range(200):
        if client.get("/health").status_code == 200:
            return
        time.sleep(0.02)
    raise AssertionError("the fake engine never became ready")


def post_page(client: TestClient, payload: bytes | None = None, page: str = "1", **kwargs):
    return client.post(
        "/v1/ocr/page",
        files={"file": ("original.bin", payload if payload is not None else png())},
        data={"page": page},
        **kwargs,
    )


def test_live_is_always_200_and_health_reports_the_loaded_model():
    client, _ = make_client()
    with client:
        assert client.get("/health/live").status_code == 200
        wait_until_ready(client)

        body = client.get("/health").json()

    assert body["modelLoaded"] is True
    assert body["modelVersion"] == "fake-engine 1.0"
    assert body["maxConcurrency"] == 1


def test_health_is_503_while_the_model_is_loading():
    client, _ = make_client(FakeEngine(load_delay=2))
    with client:
        response = client.get("/health")

    assert response.status_code == 503
    assert response.json()["modelLoaded"] is False


def test_analyze_returns_blocks_with_confidence_and_coordinates():
    client, _ = make_client()
    with client:
        wait_until_ready(client)
        response = post_page(client, headers={"X-Correlation-Id": "corr-abcdef123456"})

    body = response.json()
    assert response.status_code == 200
    assert response.headers["X-Correlation-Id"] == "corr-abcdef123456"
    assert body["page"] == 1
    assert body["pageCount"] == 1
    assert body["imageWidth"] == 200
    assert body["blocks"][0]["text"] == "111.444.777-35"
    assert body["blocks"][0]["confidence"] == 0.999
    assert len(body["blocks"][0]["boundingBox"]) == 8
    assert body["modelVersion"] == "fake-engine 1.0"
    assert body["raw"] == {"rec_texts": ["111.444.777-35"]}


def test_a_correlation_id_that_is_not_boring_is_replaced():
    client, _ = make_client()
    with client:
        response = client.get("/health/live", headers={"X-Correlation-Id": "x y; drop table"})

    assert response.headers["X-Correlation-Id"] != "x y; drop table"


@pytest.mark.parametrize(
    ("payload", "status", "code"),
    [
        (b"MZ\x90\x00 executable", 415, "UNSUPPORTED_FORMAT"),
        (b"\x89PNG\r\n\x1a\ngarbage", 422, "UNREADABLE_FILE"),
    ],
)
def test_bad_files_answer_problem_json(payload, status, code):
    client, _ = make_client()
    with client:
        wait_until_ready(client)
        response = post_page(client, payload)

    assert response.status_code == status
    assert response.headers["content-type"].startswith("application/problem+json")
    assert response.json()["errorCode"] == code
    assert response.json()["correlationId"]


def test_page_out_of_range_is_422():
    client, _ = make_client()
    with client:
        wait_until_ready(client)
        response = post_page(client, page="5")

    assert response.status_code == 422
    assert response.json()["errorCode"] == "PAGE_OUT_OF_RANGE"


def test_before_the_model_is_loaded_the_answer_is_503_with_retry_after():
    client, _ = make_client(FakeEngine(load_delay=2))
    with client:
        response = post_page(client)

    assert response.status_code == 503
    assert response.json()["errorCode"] == "OCR_MODEL_NOT_READY"
    assert response.headers["Retry-After"] == "5"


def test_slow_page_answers_504_and_keeps_its_slot_until_the_thread_ends():
    engine = FakeEngine(delay=0.8)
    client, _ = make_client(engine, page_timeout_seconds=0.3)

    with client:
        wait_until_ready(client)
        first = post_page(client)
        # The first inference is still running in its thread, so the only slot is still taken: the
        # second call cannot start inference and gives up waiting for the slot.
        second = post_page(client)

    assert first.status_code == 504
    assert first.json()["errorCode"] == "OCR_PAGE_TIMEOUT"
    assert second.status_code == 503
    assert second.json()["errorCode"] == "OCR_BUSY"
    assert engine.peak_active == 1


def test_concurrent_requests_never_exceed_the_configured_concurrency():
    engine = FakeEngine(delay=0.15)
    client, _ = make_client(engine, page_timeout_seconds=10.0, max_concurrency=1)

    with client:
        wait_until_ready(client)
        results: list[int] = []

        def call() -> None:
            results.append(post_page(client).status_code)

        threads = [threading.Thread(target=call) for _ in range(4)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()

    assert results == [200, 200, 200, 200]
    assert engine.peak_active == 1
