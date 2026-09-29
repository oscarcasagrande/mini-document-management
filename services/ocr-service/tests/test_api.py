import io
import threading
import time

import pytest
from fastapi.testclient import TestClient
from PIL import Image

from app.engine import EngineOutput, RecognizedBlock
from app.main import create_app
from app.settings import Settings
from app.structure import StructureError

from .images import cpf_card
from .pdfs import build as build_pdf


class FakeEngine:
    """Stands in for PaddleOCR: deterministic and free of the vision stack."""

    model_version = "fake-engine 1.0"

    def __init__(self, delay: float = 0.0, load_delay: float = 0.0, blocks: list[RecognizedBlock] | None = None) -> None:
        self.delay = delay
        self.load_delay = load_delay
        self.blocks = blocks
        self.active = 0
        self.peak_active = 0
        self.calls = 0
        self.sizes: list[tuple[int, int]] = []
        self._lock = threading.Lock()

    def load(self) -> None:
        time.sleep(self.load_delay)

    def recognize(self, image: Image.Image) -> EngineOutput:
        with self._lock:
            self.active += 1
            self.calls += 1
            self.sizes.append(image.size)
            self.peak_active = max(self.peak_active, self.active)

        try:
            time.sleep(self.delay)
        finally:
            with self._lock:
                self.active -= 1

        if self.blocks is not None:
            return EngineOutput(list(self.blocks), {"rec_texts": [block.text for block in self.blocks]})

        return EngineOutput(
            [RecognizedBlock("111.444.777-35", 0.999, [1.0, 2.0, 30.0, 2.0, 30.0, 12.0, 1.0, 12.0])],
            {"rec_texts": ["111.444.777-35"]},
        )


class FakeStructureEngine:
    """Stands in for the PP-StructureV3 child process."""

    model_version = "fake-structure 1.0"

    def __init__(self, error: StructureError | None = None) -> None:
        self.error = error
        self.calls = 0
        self.prepared = threading.Event()

    @property
    def ready(self) -> bool:
        return self.prepared.is_set()

    def prepare(self) -> None:
        self.prepared.set()

    def recognize(self, image: Image.Image, *, timeout_seconds: float) -> EngineOutput:
        self.calls += 1
        if self.error is not None:
            raise self.error
        return EngineOutput(
            [RecognizedBlock("ITEM 001", 0.95, [0.0, 0.0, 50.0, 0.0, 50.0, 20.0, 0.0, 20.0])],
            {"table_res_list": [{"pred_html": "<table></table>"}]},
        )


def low_confidence_grid(rows: int = 4, columns: int = 4) -> list[RecognizedBlock]:
    blocks = []
    for row in range(rows):
        for column in range(columns):
            left, top = 20.0 + column * 45, 10.0 + row * 20
            blocks.append(RecognizedBlock(f"c{row}{column}", 0.55, [left, top, left + 30, top, left + 30, top + 12, left, top + 12]))
    return blocks


def png() -> bytes:
    buffer = io.BytesIO()
    Image.new("RGB", (200, 100), (255, 255, 255)).save(buffer, format="PNG")
    return buffer.getvalue()


def make_client(
    engine: FakeEngine | None = None, structure: FakeStructureEngine | None = None, **settings
) -> tuple[TestClient, FakeEngine]:
    engine = engine or FakeEngine()
    app = create_app(engine=engine, settings=Settings(log_level="WARNING", **settings), structure_engine=structure)
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
    # The RF-009 fields are always present, false/0 when nothing was done.
    assert body["hasNativeTextLayer"] is False
    assert body["rotationDegrees"] == 0
    assert body["deskewed"] is False
    assert body["processedWithStructure"] is False


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


def wait_until_structure_ready(client: TestClient) -> None:
    for _ in range(200):
        if client.get("/health").json().get("structureReady"):
            return
        time.sleep(0.02)
    raise AssertionError("the fake structure engine never became ready")


def test_a_digital_pdf_page_is_read_from_its_text_layer_without_the_engine():
    client, engine = make_client()
    with client:
        wait_until_ready(client)
        response = post_page(client, build_pdf("text"))

    body = response.json()
    assert response.status_code == 200
    assert engine.calls == 0
    assert body["hasNativeTextLayer"] is True
    assert body["rotationDegrees"] == 0
    assert body["deskewed"] is False
    assert body["processedWithStructure"] is False
    assert body["raw"]["source"] == "pdfplumber"
    # Same pixel space as a rasterized A4 page at 200 DPI.
    assert (body["imageWidth"], body["imageHeight"]) == (1654, 2339)
    texts = [block["text"] for block in body["blocks"]]
    assert "111.444.777-35" in texts
    for block in body["blocks"]:
        assert block["confidence"] == 1.0
        assert len(block["boundingBox"]) == 8
        xs, ys = block["boundingBox"][0::2], block["boundingBox"][1::2]
        assert 0 <= min(xs) < max(xs) <= body["imageWidth"]
        assert 0 <= min(ys) < max(ys) <= body["imageHeight"]


def test_a_scanned_pdf_page_still_goes_through_the_engine():
    client, engine = make_client()
    with client:
        wait_until_ready(client)
        response = post_page(client, build_pdf("image"))

    assert response.status_code == 200
    assert engine.calls == 1
    assert response.json()["hasNativeTextLayer"] is False
    assert response.json()["raw"] == {"rec_texts": ["111.444.777-35"]}


def test_each_page_of_a_mixed_pdf_takes_its_own_path():
    content = build_pdf("image", "text")
    client, engine = make_client()
    with client:
        wait_until_ready(client)
        scanned = post_page(client, content, page="1").json()
        native = post_page(client, content, page="2").json()

    assert engine.calls == 1
    assert scanned["hasNativeTextLayer"] is False
    assert native["hasNativeTextLayer"] is True
    assert scanned["pageCount"] == native["pageCount"] == 2


def encoded(image: Image.Image) -> bytes:
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()


def test_a_sideways_image_is_turned_upright_before_the_engine_sees_it():
    card = cpf_card()
    upright_size = card.size
    sideways = encoded(card.rotate(90, expand=True))

    client, engine = make_client()
    with client:
        wait_until_ready(client)
        body = post_page(client, sideways).json()

    assert body["rotationDegrees"] == 90
    assert body["deskewed"] is False
    assert engine.sizes == [upright_size]
    assert (body["imageWidth"], body["imageHeight"]) == upright_size


def test_a_tilted_image_is_deskewed_before_the_engine_sees_it():
    tilted = encoded(cpf_card().rotate(10, expand=True, fillcolor=(255, 255, 255)))

    client, _ = make_client()
    with client:
        wait_until_ready(client)
        body = post_page(client, tilted).json()

    assert body["deskewed"] is True
    assert body["rotationDegrees"] == 0


def test_orientation_correction_can_be_switched_off():
    sideways = encoded(cpf_card().rotate(90, expand=True))

    client, _ = make_client(orientation_correction=False)
    with client:
        wait_until_ready(client)
        body = post_page(client, sideways).json()

    assert body["rotationDegrees"] == 0


def test_a_low_confidence_table_is_re_read_by_structure_when_enabled():
    structure = FakeStructureEngine()
    client, engine = make_client(FakeEngine(blocks=low_confidence_grid()), structure, use_structure_for_tables=True)
    with client:
        wait_until_ready(client)
        wait_until_structure_ready(client)
        body = post_page(client).json()

    assert structure.calls == 1
    assert body["processedWithStructure"] is True
    # Blocks are replaced, and both engines' payloads are kept (RF-008).
    assert [block["text"] for block in body["blocks"]] == ["ITEM 001"]
    assert body["raw"]["v5"]["rec_texts"][0] == "c00"
    assert body["raw"]["structureV3"] == {"table_res_list": [{"pred_html": "<table></table>"}]}


def test_structure_is_never_called_when_the_flag_is_off():
    structure = FakeStructureEngine()
    client, _ = make_client(FakeEngine(blocks=low_confidence_grid()), structure, use_structure_for_tables=False)
    with client:
        wait_until_ready(client)
        body = post_page(client).json()

    assert structure.calls == 0
    assert body["processedWithStructure"] is False
    assert client.app is not None


def test_structure_is_not_called_for_a_page_that_is_not_a_table():
    structure = FakeStructureEngine()
    client, _ = make_client(FakeEngine(), structure, use_structure_for_tables=True)
    with client:
        wait_until_ready(client)
        wait_until_structure_ready(client)
        body = post_page(client).json()

    assert structure.calls == 0
    assert body["processedWithStructure"] is False


def test_structure_is_not_called_for_a_table_read_with_high_confidence():
    confident = [RecognizedBlock(b.text, 0.99, b.bounding_box) for b in low_confidence_grid()]
    structure = FakeStructureEngine()
    client, _ = make_client(FakeEngine(blocks=confident), structure, use_structure_for_tables=True)
    with client:
        wait_until_ready(client)
        wait_until_structure_ready(client)
        post_page(client)

    assert structure.calls == 0


def test_a_table_above_the_megapixel_ceiling_keeps_the_v5_result():
    structure = FakeStructureEngine()
    client, _ = make_client(
        FakeEngine(blocks=low_confidence_grid()), structure, use_structure_for_tables=True, structure_max_megapixels=0.01
    )
    with client:
        wait_until_ready(client)
        wait_until_structure_ready(client)
        response = post_page(client)

    assert response.status_code == 200
    assert structure.calls == 0
    assert response.json()["processedWithStructure"] is False
    assert response.json()["raw"]["rec_texts"][0] == "c00"


@pytest.mark.parametrize("failure", [StructureError("KILLED", "out of memory"), RuntimeError("anything else")])
def test_a_structure_failure_falls_back_to_the_v5_result(failure):
    structure = FakeStructureEngine(error=failure)
    client, _ = make_client(FakeEngine(blocks=low_confidence_grid()), structure, use_structure_for_tables=True)
    with client:
        wait_until_ready(client)
        wait_until_structure_ready(client)
        response = post_page(client)

    body = response.json()
    assert response.status_code == 200
    assert structure.calls == 1
    assert body["processedWithStructure"] is False
    assert [block["text"] for block in body["blocks"]][0] == "c00"
    assert body["raw"] == {"rec_texts": [block.text for block in low_confidence_grid()]}


def test_health_reports_whether_structure_is_enabled():
    client, _ = make_client()
    with client:
        body = client.get("/health/live").json()

    assert body["structureEnabled"] is False
    assert body["structureReady"] is False
