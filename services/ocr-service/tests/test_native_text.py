import io

import pypdfium2 as pdfium
import pytest

from app.native_text import read_native_page, rendered_size
from app.pages import render_page

from .pdfs import build


def read(content: bytes, page: int = 1, **overrides):
    options = {"dpi": 200, "max_side": 3200, "min_chars": 20} | overrides
    return read_native_page(content, page, **options)


def box(block):
    xs, ys = block.bounding_box[0::2], block.bounding_box[1::2]
    return min(xs), min(ys), max(xs), max(ys)


def test_a_digital_page_is_read_from_its_text_layer():
    content = build("text")

    native = read(content)

    assert native is not None
    texts = [block.text for block in native.blocks]
    assert "NOME" in texts
    assert "MARIA APARECIDA DA SILVA SOUZA" in texts
    assert "111.444.777-35" in texts
    assert all(block.confidence == 1.0 for block in native.blocks)
    assert native.raw["source"] == "pdfplumber"
    assert native.raw["lines"][0]["text"] == native.blocks[0].text


def test_boxes_are_in_the_pixel_space_the_rasterizer_would_produce():
    content = build("text")

    native = read(content)
    rendered = render_page(content, 1, pdf_dpi=200, max_side=3200)

    assert (native.width, native.height) == rendered.image.size
    # The labels are drawn at x = 72 pt, which is 200 px at 200 DPI.
    label = next(block for block in native.blocks if block.text == "NOME")
    left, top, right, bottom = box(label)
    assert left == pytest.approx(200.0, abs=1.0)
    assert 0 < top < bottom < native.height
    assert all(0 <= value <= native.width for value in label.bounding_box[0::2])


def test_boxes_follow_the_side_limit_like_the_rasterizer():
    content = build("text")

    native = read(content, max_side=1000)
    rendered = render_page(content, 1, pdf_dpi=200, max_side=1000)

    assert (native.width, native.height) == rendered.image.size
    label = next(block for block in native.blocks if block.text == "NOME")
    assert box(label)[0] == pytest.approx(72 * 1000 / 2339 * 200 / 72, abs=1.0)


@pytest.mark.parametrize(("width", "height", "dpi"), [(595.28, 841.89, 200), (612, 792, 200), (595.28, 841.89, 150), (1000, 400, 300)])
def test_the_computed_size_matches_pdfium_rendering(width, height, dpi):
    document = pdfium.PdfDocument.new()
    document.new_page(width, height)
    buffer = io.BytesIO()
    document.save(buffer)

    rendered = render_page(buffer.getvalue(), 1, pdf_dpi=dpi, max_side=3200)

    assert rendered_size(width, height, dpi=dpi, max_side=3200)[:2] == rendered.image.size


def test_words_far_apart_on_one_line_become_separate_blocks():
    native = read(build("text"))

    assert "CAMPO DA DIREITA" in [block.text for block in native.blocks]
    assert not any("SOUZA CAMPO" in block.text for block in native.blocks)


def test_a_diagonal_watermark_is_left_out():
    native = read(build("text"))

    assert not any("AMOSTRA" in block.text for block in native.blocks)
    assert native.raw["skippedNotHorizontalChars"] > 0


def test_blocks_come_in_reading_order():
    native = read(build("text"))
    tops = [box(block)[1] for block in native.blocks]

    assert tops == sorted(tops)


@pytest.mark.parametrize(
    "kind",
    ["image", "empty", "number", "stamped"],
    ids=["scanned page", "empty page", "page number only", "scan with a digital stamp"],
)
def test_pages_without_real_text_go_to_ocr(kind):
    assert read(build(kind)) is None


def test_the_minimum_is_configurable():
    content = build("number")

    assert read(content, min_chars=5) is not None


def test_a_rotated_page_goes_to_ocr():
    assert read(build("text", rotate=90)) is None


def test_the_requested_page_of_a_mixed_pdf_is_the_one_read():
    content = build("image", "text")

    assert read(content, page=1) is None
    second = read(content, page=2)
    assert second is not None
    assert second.page_count == 2


def test_a_page_beyond_the_end_or_a_broken_pdf_is_left_to_the_rasterizer():
    assert read(build("text"), page=2) is None
    assert read(b"%PDF-1.7 not really a pdf") is None
