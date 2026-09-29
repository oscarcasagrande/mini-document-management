"""PP-StructureV3 runner without PP-StructureV3: the payload conversion, and the child process
contract exercised with stand-in executables that succeed, get killed, crash or hang."""

import json
import os
import stat
from pathlib import Path

import pytest
from PIL import Image

from app.structure import StructureError, SubprocessStructureEngine, structure_blocks


def payload() -> dict:
    """Shape of a real PP-StructureV3 page (pagina-tabela.png), cut down to a 2 x 2 table."""
    return {
        "table_res_list": [
            {
                "cell_box_list": [[100, 300, 300, 360], [300, 300, 600, 360], [100, 360, 300, 420], [300, 360, 600, 420]],
                "table_ocr_pred": {
                    "rec_texts": ["ITEM", "VALOR", "001", "216,00", "linha", "partida"],
                    "rec_scores": [0.99, 0.97, 0.5, 0.7, 0.9, 0.8],
                    "rec_polys": [
                        [[110, 310], [200, 310], [200, 350], [110, 350]],
                        [[310, 310], [420, 310], [420, 350], [310, 350]],
                        [[110, 370], [170, 370], [170, 385], [110, 385]],
                        [[480, 370], [590, 370], [590, 410], [480, 410]],
                        [[110, 390], [170, 390], [170, 410], [110, 410]],
                        [[700, 900], [800, 900], [800, 930], [700, 930]],
                    ],
                },
            }
        ],
        "overall_ocr_res": {
            "rec_texts": ["DEMONSTRATIVO", "ITEM", "Rodape da pagina"],
            "rec_scores": [0.995, 0.99, 0.98],
            "rec_polys": [
                [[100, 100], [700, 100], [700, 140], [100, 140]],
                [[110, 310], [200, 310], [200, 350], [110, 350]],
                [[100, 1000], [500, 1000], [500, 1030], [100, 1030]],
            ],
        },
    }


def test_table_cells_become_one_block_each_and_outside_text_stays_line_by_line():
    blocks = structure_blocks(payload())
    texts = [block.text for block in blocks]

    assert texts[0] == "DEMONSTRATIVO"
    assert texts[-1] == "Rodape da pagina"
    # The page-wide OCR of "ITEM" is inside the table: only the cell block remains.
    assert texts.count("ITEM") == 1
    # Two lines in one cell are joined top to bottom, with the mean confidence.
    joined = next(block for block in blocks if block.text == "001 linha")
    assert joined.confidence == pytest.approx(0.7)
    assert joined.bounding_box == [100.0, 360.0, 300.0, 360.0, 300.0, 420.0, 100.0, 420.0]
    # Table text that fell in no cell is kept as its own block, not lost.
    assert "partida" in texts


def test_an_empty_payload_has_no_blocks():
    assert structure_blocks({}) == []


def fake_python(tmp_path: Path, body: str) -> str:
    """An executable that stands in for the interpreter: receives -m app.structure <args>."""
    script = tmp_path / "fake-python"
    script.write_text("#!/bin/sh\n" + body + "\n")
    script.chmod(script.stat().st_mode | stat.S_IXUSR)
    return str(script)


def ready_engine(tmp_path: Path, body: str) -> SubprocessStructureEngine:
    engine = SubprocessStructureEngine(model_cache_dir=str(tmp_path), python=fake_python(tmp_path, body))
    engine.prepare()  # the fake exits 0 for "prefetch" too
    return engine


pytestmark = pytest.mark.skipif(os.name != "posix", reason="the stand-in interpreter is a shell script")


def test_a_successful_child_result_is_converted(tmp_path):
    result = tmp_path / "result.json"
    result.write_text(json.dumps(payload()))
    # $5 is the output path: -m app.structure predict <image> <output>
    engine = ready_engine(tmp_path, f'[ "$3" = predict ] && cp {result} "$5"; exit 0')

    output = engine.recognize(Image.new("RGB", (50, 50), (255, 255, 255)), timeout_seconds=10)

    assert any(block.text == "001 linha" for block in output.blocks)
    assert output.raw["table_res_list"]


def test_a_child_killed_by_the_kernel_is_reported_as_killed(tmp_path):
    engine = ready_engine(tmp_path, '[ "$3" = predict ] && kill -9 $$; exit 0')

    with pytest.raises(StructureError) as error:
        engine.recognize(Image.new("RGB", (50, 50)), timeout_seconds=10)

    assert error.value.code == "KILLED"


def test_a_child_that_fails_or_writes_nothing_is_reported_as_failed(tmp_path):
    engine = ready_engine(tmp_path, '[ "$3" = predict ] && exit 1; exit 0')

    with pytest.raises(StructureError) as error:
        engine.recognize(Image.new("RGB", (50, 50)), timeout_seconds=10)

    assert error.value.code == "FAILED"


def test_a_child_past_the_deadline_is_killed(tmp_path):
    engine = ready_engine(tmp_path, '[ "$3" = predict ] && exec sleep 30; exit 0')

    with pytest.raises(StructureError) as error:
        engine.recognize(Image.new("RGB", (50, 50)), timeout_seconds=1)

    assert error.value.code == "TIMEOUT"


def test_nothing_is_sent_before_the_models_are_downloaded(tmp_path):
    engine = SubprocessStructureEngine(model_cache_dir=str(tmp_path), python=fake_python(tmp_path, "exit 0"))

    with pytest.raises(StructureError) as error:
        engine.recognize(Image.new("RGB", (50, 50)), timeout_seconds=10)

    assert error.value.code == "NOT_READY"
    assert engine.ready is False


def test_a_failed_prefetch_leaves_the_feature_unavailable(tmp_path):
    engine = SubprocessStructureEngine(model_cache_dir=str(tmp_path), python=fake_python(tmp_path, "exit 3"))

    with pytest.raises(StructureError) as error:
        engine.prepare()

    assert error.value.code == "PREFETCH_FAILED"
    assert engine.ready is False
