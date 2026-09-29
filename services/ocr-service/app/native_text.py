"""Reads the text a PDF page already carries, so a digital PDF skips rasterization and OCR (RF-009).

The page is answered in the same shape as an OCR'd page: one block per text segment, with a bounding
box in the pixel space the page *would* have had if it had been rasterized at ``OCR_PDF_DPI`` (and
capped at ``OCR_MAX_IMAGE_SIDE``). The .NET extraction measures label and value geometry on these
boxes, so a native page and a scanned page of the same layout must look alike to it.

A page is only read natively when its text layer is real content. The checks, each one a way a text
layer misleads:

- **Too little text.** Fewer than ``min_chars`` letters and digits: an empty layer, a page number, a
  lone form label. The page goes to OCR.
- **Garbage.** Glyphs without a Unicode mapping come out as ``(cid:12)``; those are dropped, and a
  layer that is still mostly symbols is not text.
- **A scan with a stamp.** A page mostly covered by images whose text is short (below
  ``SCAN_STAMP_MAX_CHARS``) is a scanned page with a digital signature stamp or a header typed over
  it: reading only the stamp would lose the scanned body. A searchable scan (image plus a full
  invisible OCR layer) has plenty of text and is read natively.
- **Words that are not horizontal** (a diagonal watermark, a vertical margin note) are left out of the
  blocks and counted in ``raw``; the OCR path does not read them as lines either.
- **Rotated pages** (``/Rotate`` other than 0). pdfplumber reports their words in rotated coordinates
  but keeps the unrotated page size, so the boxes cannot be placed reliably; they go to OCR, where the
  orientation correction turns them upright.
"""

from __future__ import annotations

import io
import math
import re
import statistics
from dataclasses import dataclass
from typing import Any

from .engine import RecognizedBlock

# See the module docstring. 50% of the page under images and less than this many letters and digits
# means "scan with a stamp", not "digital page".
SCAN_IMAGE_COVERAGE = 0.5
SCAN_STAMP_MAX_CHARS = 200

# Share of letters and digits among the non-space characters below which a layer is garbage.
MIN_ALNUM_SHARE = 0.5

# A horizontal gap wider than this many line heights splits a line into two blocks, the way the OCR
# detector splits a label from a value printed further along the same line.
SEGMENT_GAP_LINE_HEIGHTS = 1.0

# A character drawn at more than this angle is not part of a horizontal line.
MAX_CHAR_TILT_DEGREES = 2.0

_CID = re.compile(r"\(cid:\d+\)")


@dataclass(frozen=True)
class NativePage:
    page_count: int
    width: int
    height: int
    blocks: list[RecognizedBlock]
    raw: dict[str, Any]


@dataclass(frozen=True)
class _Word:
    text: str
    x0: float
    top: float
    x1: float
    bottom: float


def rendered_size(width_pt: float, height_pt: float, *, dpi: int, max_side: int) -> tuple[int, int, float]:
    """Pixel size the rasterizer would produce for this page, and the points-to-pixels factor.

    pypdfium2 renders ``ceil(points * dpi / 72)`` pixels per side; ``pages._limit_side`` then shrinks
    anything above ``max_side``. Both steps are reproduced so native boxes land where OCR boxes would.
    """
    scale = dpi / 72.0
    width = math.ceil(width_pt * scale)
    height = math.ceil(height_pt * scale)

    longest = max(width, height)
    if max_side > 0 and longest > max_side:
        ratio = max_side / longest
        width, height = max(1, round(width * ratio)), max(1, round(height * ratio))
        scale *= ratio

    return width, height, scale


def read_native_page(content: bytes, page: int, *, dpi: int, max_side: int, min_chars: int) -> NativePage | None:
    """The page read from its text layer, or None when it must go to OCR.

    Never raises for a malformed PDF: whatever pdfplumber cannot parse is left to the rasterizer,
    which reports the page count and decoding errors the way the OCR path always has.
    """
    try:
        import pdfplumber
    except ImportError:
        return None

    try:
        with pdfplumber.open(io.BytesIO(content)) as pdf:
            page_count = len(pdf.pages)
            if page < 1 or page > page_count:
                return None
            pdf_page = pdf.pages[page - 1]
            if (pdf_page.rotation or 0) % 360 != 0:
                return None

            # Diagonal watermarks and vertical margin notes ("documento assinado digitalmente...")
            # are not lines of the page: their boxes would be tall slivers across the layout.
            skipped_not_horizontal = sum(1 for char in pdf_page.chars if not _horizontal(char))
            horizontal_page = pdf_page.filter(_horizontal) if skipped_not_horizontal else pdf_page
            words = [
                _Word(_CID.sub("", str(word["text"])), float(word["x0"]), float(word["top"]), float(word["x1"]), float(word["bottom"]))
                for word in horizontal_page.extract_words(keep_blank_chars=False, use_text_flow=False)
            ]
            words = [word for word in words if word.text.strip()]
            page_width, page_height = float(pdf_page.width), float(pdf_page.height)
            origin_x, origin_top = float(pdf_page.bbox[0]), float(pdf_page.bbox[1])
            image_share = _image_coverage(pdf_page.images, page_width * page_height)
            version = getattr(pdfplumber, "__version__", "unknown")
    except Exception:  # noqa: BLE001 - any parser failure means "let OCR read it"
        return None

    text = "".join(word.text for word in words)
    alnum = sum(1 for character in text if character.isalnum())
    if alnum < min_chars:
        return None
    if alnum < MIN_ALNUM_SHARE * len(text):
        return None
    if image_share >= SCAN_IMAGE_COVERAGE and alnum < SCAN_STAMP_MAX_CHARS:
        return None

    width, height, factor = rendered_size(page_width, page_height, dpi=dpi, max_side=max_side)
    segments = _segments(words)

    blocks = [
        RecognizedBlock(
            segment.text,
            # The text is what the PDF says, not what a model inferred: there is no uncertainty to report.
            1.0,
            _polygon(segment, factor, origin_x, origin_top),
        )
        for segment in segments
    ]

    raw: dict[str, Any] = {
        "source": "pdfplumber",
        "version": version,
        "pageWidthPt": round(page_width, 3),
        "pageHeightPt": round(page_height, 3),
        "pointsToPixels": round(factor, 6),
        "imageCoverage": round(image_share, 4),
        "skippedNotHorizontalChars": skipped_not_horizontal,
        "lines": [
            {
                "text": segment.text,
                "x0": round(segment.x0, 2),
                "top": round(segment.top, 2),
                "x1": round(segment.x1, 2),
                "bottom": round(segment.bottom, 2),
            }
            for segment in segments
        ],
    }

    return NativePage(page_count, width, height, blocks, raw)


def _horizontal(obj: dict[str, Any]) -> bool:
    """False for a character drawn at an angle. pdfminer's ``upright`` only rejects vertical text, so
    the angle is read from the character's text matrix."""
    if obj.get("object_type") != "char":
        return True
    matrix = obj.get("matrix")
    if not matrix or len(matrix) < 2:
        return True
    return abs(math.degrees(math.atan2(float(matrix[1]), float(matrix[0])))) <= MAX_CHAR_TILT_DEGREES


def _image_coverage(images: list[dict[str, Any]], page_area: float) -> float:
    """Share of the page under images. Overlaps are counted twice; capped at 1, which is all it decides."""
    if page_area <= 0:
        return 0.0
    covered = 0.0
    for image in images:
        try:
            covered += max(0.0, float(image["x1"]) - float(image["x0"])) * max(0.0, float(image["bottom"]) - float(image["top"]))
        except (KeyError, TypeError, ValueError):
            continue
    return min(1.0, covered / page_area)


def _segments(words: list[_Word]) -> list[_Word]:
    """Groups words into lines, then cuts each line at wide gaps, in reading order."""
    if not words:
        return []

    heights = [word.bottom - word.top for word in words if word.bottom > word.top]
    unit = statistics.median(heights) if heights else 10.0

    lines: list[list[_Word]] = []
    for word in sorted(words, key=lambda w: ((w.top + w.bottom) / 2, w.x0)):
        centre = (word.top + word.bottom) / 2
        if lines:
            last = lines[-1]
            last_centre = sum((w.top + w.bottom) / 2 for w in last) / len(last)
            if abs(centre - last_centre) <= unit * 0.5:
                last.append(word)
                continue
        lines.append([word])

    segments: list[_Word] = []
    for line in lines:
        line.sort(key=lambda w: w.x0)
        current = [line[0]]
        for word in line[1:]:
            if word.x0 - current[-1].x1 > unit * SEGMENT_GAP_LINE_HEIGHTS:
                segments.append(_merge(current))
                current = [word]
            else:
                current.append(word)
        segments.append(_merge(current))

    return segments


def _merge(words: list[_Word]) -> _Word:
    return _Word(
        " ".join(word.text for word in words),
        min(word.x0 for word in words),
        min(word.top for word in words),
        max(word.x1 for word in words),
        max(word.bottom for word in words),
    )


def _polygon(segment: _Word, factor: float, origin_x: float, origin_top: float) -> list[float]:
    left = round((segment.x0 - origin_x) * factor, 1)
    right = round((segment.x1 - origin_x) * factor, 1)
    top = round((segment.top - origin_top) * factor, 1)
    bottom = round((segment.bottom - origin_top) * factor, 1)
    return [left, top, right, top, right, bottom, left, bottom]
