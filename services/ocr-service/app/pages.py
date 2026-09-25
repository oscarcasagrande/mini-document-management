"""Turns an uploaded file into the RGB image of one page.

The worker sends the original bytes and the page number; rasterizing here keeps the .NET side free of
imaging dependencies and lets the page limit and DPI live next to the engine they were measured for.
"""

from __future__ import annotations

import io
from dataclasses import dataclass

from PIL import Image, ImageOps

PDF_SIGNATURE = b"%PDF-"
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
JPEG_SIGNATURE = b"\xff\xd8\xff"
TIFF_SIGNATURES = (b"II*\x00", b"MM\x00*")

# Pillow refuses images above this many pixels as a decompression bomb guard; the value is raised
# from the default because a 25 MB upload of a dense scan is legitimate, but it stays bounded.
Image.MAX_IMAGE_PIXELS = 200_000_000


class UnsupportedFormatError(ValueError):
    """The bytes are not a PDF, PNG, JPEG or TIFF."""


class PageOutOfRangeError(ValueError):
    """The requested page does not exist in the file."""

    def __init__(self, page: int, page_count: int) -> None:
        super().__init__(f"page {page} requested but the file has {page_count} page(s)")
        self.page = page
        self.page_count = page_count


class UnreadableFileError(ValueError):
    """The signature is valid but the file cannot be decoded."""


@dataclass(frozen=True)
class RenderedPage:
    image: Image.Image
    page_count: int


def detect_kind(content: bytes) -> str:
    """Identifies the format by signature, never by the name or the announced content type."""
    if content.startswith(PDF_SIGNATURE):
        return "pdf"
    if content.startswith(PNG_SIGNATURE):
        return "png"
    if content.startswith(JPEG_SIGNATURE):
        return "jpeg"
    if content.startswith(TIFF_SIGNATURES):
        return "tiff"
    raise UnsupportedFormatError("only PDF, PNG, JPEG and TIFF are accepted")


def render_page(content: bytes, page: int, *, pdf_dpi: int, max_side: int) -> RenderedPage:
    """Returns page ``page`` (1 based) as an RGB image no larger than ``max_side`` pixels."""
    if page < 1:
        raise PageOutOfRangeError(page, 0)

    kind = detect_kind(content)

    try:
        if kind == "pdf":
            rendered = _render_pdf_page(content, page, pdf_dpi)
        else:
            rendered = _render_image_page(content, page)
    except (PageOutOfRangeError, UnsupportedFormatError):
        raise
    except Exception as exception:  # noqa: BLE001 - decoders raise many unrelated types
        raise UnreadableFileError(f"the {kind} file could not be decoded") from exception

    return RenderedPage(_limit_side(rendered.image, max_side), rendered.page_count)


def _render_pdf_page(content: bytes, page: int, dpi: int) -> RenderedPage:
    import pypdfium2 as pdfium

    document = pdfium.PdfDocument(content)
    try:
        page_count = len(document)
        if page > page_count:
            raise PageOutOfRangeError(page, page_count)

        pdf_page = document[page - 1]
        try:
            bitmap = pdf_page.render(scale=dpi / 72.0)
            image = bitmap.to_pil().convert("RGB")
        finally:
            pdf_page.close()
    finally:
        document.close()

    return RenderedPage(image, page_count)


def _render_image_page(content: bytes, page: int) -> RenderedPage:
    with Image.open(io.BytesIO(content)) as source:
        page_count = getattr(source, "n_frames", 1)
        if page > page_count:
            raise PageOutOfRangeError(page, page_count)

        source.seek(page - 1)
        # Phone photos carry their rotation in EXIF; without this the detector sees them sideways.
        oriented = ImageOps.exif_transpose(source)
        image = _flatten_to_rgb(oriented.copy())

    return RenderedPage(image, page_count)


def _flatten_to_rgb(image: Image.Image) -> Image.Image:
    """Composites transparency over white: a black background would swallow dark text."""
    if image.mode in {"RGBA", "LA"} or (image.mode == "P" and "transparency" in image.info):
        rgba = image.convert("RGBA")
        background = Image.new("RGB", rgba.size, (255, 255, 255))
        background.paste(rgba, mask=rgba.getchannel("A"))
        return background

    return image.convert("RGB")


def _limit_side(image: Image.Image, max_side: int) -> Image.Image:
    longest = max(image.size)
    if max_side <= 0 or longest <= max_side:
        return image

    ratio = max_side / longest
    size = (max(1, round(image.width * ratio)), max(1, round(image.height * ratio)))

    return image.resize(size, Image.Resampling.LANCZOS)
