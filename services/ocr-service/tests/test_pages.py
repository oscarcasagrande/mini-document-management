import io

import pypdfium2 as pdfium
import pytest
from PIL import Image

from app.pages import (
    PageOutOfRangeError,
    UnreadableFileError,
    UnsupportedFormatError,
    detect_kind,
    render_page,
)


def png_bytes(size=(200, 100), color=(255, 255, 255)) -> bytes:
    buffer = io.BytesIO()
    Image.new("RGB", size, color).save(buffer, format="PNG")
    return buffer.getvalue()


def tiff_bytes(pages: int) -> bytes:
    frames = [Image.new("RGB", (120, 80), (index * 40, 0, 0)) for index in range(pages)]
    buffer = io.BytesIO()
    frames[0].save(buffer, format="TIFF", save_all=True, append_images=frames[1:])
    return buffer.getvalue()


def pdf_bytes(pages: int) -> bytes:
    document = pdfium.PdfDocument.new()
    for _ in range(pages):
        document.new_page(595, 842)  # A4 in points
    buffer = io.BytesIO()
    document.save(buffer)
    return buffer.getvalue()


def test_detects_format_by_signature_not_by_name():
    assert detect_kind(png_bytes()) == "png"
    assert detect_kind(tiff_bytes(1)) == "tiff"
    assert detect_kind(pdf_bytes(1)) == "pdf"
    assert detect_kind(b"\xff\xd8\xff\xe0rest") == "jpeg"

    with pytest.raises(UnsupportedFormatError):
        detect_kind(b"MZ\x90\x00 an executable pretending to be a document")


def test_png_page_one_is_returned_untouched():
    rendered = render_page(png_bytes((200, 100)), 1, pdf_dpi=200, max_side=3200)

    assert rendered.page_count == 1
    assert rendered.image.size == (200, 100)
    assert rendered.image.mode == "RGB"


def test_page_beyond_the_end_is_out_of_range():
    with pytest.raises(PageOutOfRangeError) as error:
        render_page(png_bytes(), 2, pdf_dpi=200, max_side=3200)

    assert error.value.page_count == 1


def test_multipage_tiff_selects_the_requested_frame():
    content = tiff_bytes(3)

    second = render_page(content, 2, pdf_dpi=200, max_side=3200)
    third = render_page(content, 3, pdf_dpi=200, max_side=3200)

    assert second.page_count == 3
    assert second.image.getpixel((0, 0)) == (40, 0, 0)
    assert third.image.getpixel((0, 0)) == (80, 0, 0)


def test_pdf_page_is_rasterized_at_the_configured_dpi():
    rendered = render_page(pdf_bytes(2), 2, pdf_dpi=200, max_side=10_000)

    assert rendered.page_count == 2
    # A4 at 200 DPI is 1654 x 2339.
    assert abs(rendered.image.width - 1654) <= 2
    assert abs(rendered.image.height - 2339) <= 2


def test_pdf_page_out_of_range_reports_the_real_count():
    with pytest.raises(PageOutOfRangeError) as error:
        render_page(pdf_bytes(2), 3, pdf_dpi=200, max_side=3200)

    assert error.value.page_count == 2


def test_large_images_are_downscaled_keeping_the_aspect_ratio():
    rendered = render_page(png_bytes((4000, 2000)), 1, pdf_dpi=200, max_side=1000)

    assert rendered.image.size == (1000, 500)


def test_transparent_png_is_flattened_over_white_not_black():
    buffer = io.BytesIO()
    Image.new("RGBA", (20, 20), (0, 0, 0, 0)).save(buffer, format="PNG")

    rendered = render_page(buffer.getvalue(), 1, pdf_dpi=200, max_side=3200)

    assert rendered.image.getpixel((5, 5)) == (255, 255, 255)


def test_corrupt_file_with_a_valid_signature_is_unreadable():
    with pytest.raises(UnreadableFileError):
        render_page(b"\x89PNG\r\n\x1a\n" + b"not really a png", 1, pdf_dpi=200, max_side=3200)
