"""Orientation correction applied to a page image before OCR (RF-009).

Two independent, deterministic corrections, no model involved:

1. **Cardinal rotation** (0, 90, 180 or 270 degrees). The horizontal projection profile (ink per row)
   of a page whose text lines run horizontally alternates between text rows and blank gaps, so its
   variance is high; turned sideways the same page smears ink evenly over the rows. Comparing the
   profile variance of the image with that of its transpose tells which **axis** the text runs on.

   Variance alone cannot tell the **direction** on that axis: the profile of a page turned 180 degrees
   is the same profile reversed, with exactly the same variance (likewise 90 against 270). Two
   geometric cues of Latin text break the tie:

   - *ascenders against descenders*: in mixed case text there is more ink above the x-height band
     (b, d, f, h, k, l, t and every capital) than below it (g, j, p, q, y);
   - *ragged right*: lines start at an aligned left margin and end wherever the words run out.

   Known limitation, accepted: a page that is all capitals and centred (a heading-only card) gives
   neither cue, so 0 cannot be told from 180 there. Upright is then kept (an upright page is by far
   the common case), and a sideways page falls back to the sign of the weak evidence available.

2. **Deskew** of a small tilt, after the cardinal step. Text lines are smeared horizontally into bars,
   their edges are found with Canny and their angle with a probabilistic Hough transform; the
   length-weighted median of the near-horizontal segments is the skew. Tilts below a noise floor are
   left alone (rotating costs an interpolation for nothing) and tilts beyond a cap are treated as a
   detection error, not as a page fed that crooked.

Everything is measured on a downscaled grayscale copy; only the final rotation touches the full
image. Nothing here reads or logs text.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np
from PIL import Image

# Longest side of the working copy. Enough for a text line of an A4 page at 200 DPI to stay ~12 px tall
# (the direction cues look inside the line), small enough for the whole analysis to take tens of ms.
ANALYSIS_MAX_SIDE = 1200

# Minimum ratio between the profile variance on the best axis and on the other one before a page is
# declared sideways. Below it the evidence is too weak to turn a page, and upright is kept.
AXIS_MIN_RATIO = 1.25

# Minimum absolute direction score (in [-1, 1]) to flip an upright-axis page by 180 degrees. Flipping
# a correct page is far worse than leaving an upside-down one, so the bar is high.
FLIP_MIN_SCORE = 0.25

# Tilts tried when comparing the two axes, so a crooked page is not mistaken for a sideways one.
AXIS_TILT_SWEEP = (-20.0, -15.0, -10.0, -6.0, -3.0, 3.0, 6.0, 10.0, 15.0, 20.0)

# Normalised profile variance the text axis must reach before a page is turned sideways. Text pages
# of the synthetic set measure 1.4 (a noisy scanned card) to 6.5; a textless scan measures 0.
MIN_AXIS_VARIANCE = 1.0

# Ascender cue magnitude from which the letters decide the direction on their own.
ASCENDER_DECISIVE = 0.3
# Spread of line starts or ends, as a share of the page width, below which the alignment cue is
# scaled down: it has to be layout-sized raggedness, not a few pixels.
ALIGNMENT_MIN_SPREAD = 0.1

# A line votes in the ascender cue only when its x-height core is at most this share of its height and
# at least TAIL_MIN_SHARE of its ink lies outside the core; and the cue speaks only when at least this
# share of the lines (and MIN_TEXT_LINES) vote. Mixed case lines measure a core of 0.70-0.84 of the
# line with 3-13% of ink in the tails; capitals measure 0.98-1.00 and 0%.
X_HEIGHT_MAX_SHARE = 0.9
TAIL_MIN_SHARE = 0.02
ASCENDER_MIN_LINE_SHARE = 0.3

# Below this magnitude the ascender cue is noise (all-capitals pages read -0.02 to 0.02).
ASCENDER_NOISE = 0.05

# Text lines the page must show before it is flipped upside down.
MIN_TEXT_LINES = 3

# Above this share of "ink" the dark class is a background (a photo, a table top), not text on paper.
MAX_INK_SHARE = 0.35

DEFAULT_DESKEW_MIN_DEGREES = 0.5
DEFAULT_DESKEW_MAX_DEGREES = 20.0

WHITE = (255, 255, 255)


@dataclass(frozen=True)
class OrientationResult:
    image: Image.Image
    # Clockwise degrees the page was turned to become upright: 0, 90, 180 or 270.
    rotation_degrees: int
    deskewed: bool
    # The tilt that was measured after the cardinal step, in degrees (positive = the text runs down to
    # the right), or None when too few lines were found to measure it. Reported for diagnostics.
    skew_degrees: float | None


def correct_orientation(
    image: Image.Image,
    *,
    detect_rotation: bool = True,
    deskew: bool = True,
    deskew_min_degrees: float = DEFAULT_DESKEW_MIN_DEGREES,
    deskew_max_degrees: float = DEFAULT_DESKEW_MAX_DEGREES,
) -> OrientationResult:
    """Returns the page upright and straight, the clockwise cardinal correction and whether it was deskewed."""
    rgb = image.convert("RGB")
    rotation = 0

    # Step 1, the axis: turn a sideways page so its lines run horizontally (provisionally 90 degrees
    # clockwise; step 3 may turn it the other way).
    sideways = detect_rotation and detect_text_axis(rgb) == "vertical"
    if sideways:
        rotation = 90
        rgb = _turn(rgb, 90)

    # Step 2, the tilt, measured on horizontal lines. It comes before the direction on purpose: a tilt
    # of half a degree already drifts a line by more than its own height across an A4 page, which
    # smears the row profile the direction cues read.
    skew: float | None = None
    deskewed = False
    if deskew:
        skew = detect_skew(rgb, max_degrees=deskew_max_degrees)
        if skew is not None and deskew_min_degrees <= abs(skew) <= deskew_max_degrees:
            rgb = rotate_fine(rgb, skew)
            deskewed = True

    # Step 3, the direction on that axis. A straight page turned 180 degrees stays straight, so the
    # deskew above holds whichever way this goes.
    if detect_rotation:
        ink = _ink_mask(_working_copy(rgb))
        # A tilt below the deskew floor is left in the image OCR reads, but the direction cues look
        # inside each line and are straightened for it anyway: a third of a degree already blurs them.
        if not deskewed and skew is not None and abs(skew) >= 0.1:
            ink = _straighten_mask(ink, skew)
        flip = _needs_flip(ink, sideways=sideways)
        if flip:
            rotation = (rotation + 180) % 360
            rgb = _turn(rgb, 180)

    return OrientationResult(rgb, rotation, deskewed, skew)


def detect_cardinal_rotation(image: Image.Image) -> int:
    """Clockwise correction (0, 90, 180, 270) that makes the text of ``image`` upright."""
    return correct_orientation(image).rotation_degrees


def detect_text_axis(image: Image.Image) -> str:
    """"horizontal" or "vertical": the axis the text lines run on, by projection profile variance.

    Ties, blank pages and pages without enough text lines answer "horizontal", so they are left alone.
    """
    ink = _ink_mask(_working_copy(image))
    if ink.sum() == 0:
        return "horizontal"

    vertical = _tilted_profile_variance(ink.T)
    # A page is only turned on clear banding: a photo, a stamp or a blank page with a border can
    # band a little on either axis without holding any line to read.
    if vertical >= MIN_AXIS_VARIANCE and vertical > _tilted_profile_variance(ink) * AXIS_MIN_RATIO:
        return "vertical"

    return "horizontal"


def _needs_flip(ink: np.ndarray, *, sideways: bool) -> bool:
    """Whether a mask with horizontal lines is upside down.

    An upright page is only flipped on strong evidence, since flipping a correct page is the worst
    outcome. A page that was sideways is upside down half the time by construction, so there the sign
    of the evidence decides, and a tie keeps the 90 degree guess.
    """
    if len(_text_lines(ink)) < MIN_TEXT_LINES:
        return False

    score = direction_score(ink)
    return score < 0 if sideways else score <= -FLIP_MIN_SCORE


def _straighten_mask(ink: np.ndarray, skew_degrees: float) -> np.ndarray:
    import cv2

    height, width = ink.shape
    matrix = cv2.getRotationMatrix2D((width / 2.0, height / 2.0), skew_degrees, 1.0)
    turned = cv2.warpAffine(ink.astype(np.uint8), matrix, (width, height), flags=cv2.INTER_NEAREST, borderValue=0)
    return turned > 0


def _turn(image: Image.Image, clockwise_degrees: int) -> Image.Image:
    # PIL turns counterclockwise; the reported value is the clockwise correction.
    return image.rotate(-clockwise_degrees, expand=True)


def direction_score(ink: np.ndarray) -> float:
    """How upright a mask with horizontal text lines looks, in [-1, 1]; negative means upside down.

    The ascender cue decides when it is clear: it is a property of the letters, measured on every
    line, and reads 0.5 to 0.8 on the mixed case pages of the synthetic set. The alignment cue is a
    property of the layout and is the fallback for all-capitals documents (cards, forms), where there
    are no ascenders to compare; it is not trusted over the letters because a table with right-aligned
    figures aligns its line ends better than its starts and reads upside down.

    When the letters lean one way, weakly, and the layout the other, the layout does not overrule them:
    the answer is the letters' sign at half weight, which never flips an upright page and still picks
    a side for a sideways one. A small, degraded table (right-aligned figures, lines 11 px tall) gave
    exactly that: ascender +0.12, alignment -1.0.
    """
    lines = _text_lines(ink)
    if not lines:
        return 0.0

    ascender = _ascender_cue(ink, lines)
    if ascender is not None and abs(ascender) >= ASCENDER_DECISIVE:
        return float(ascender)

    alignment = _alignment_cue(ink, lines)
    if alignment is None:
        return float(ascender or 0.0)
    if ascender is not None and abs(ascender) >= ASCENDER_NOISE and (ascender > 0) != (alignment > 0):
        return float(ascender) / 2
    return float(alignment)


def detect_skew(image: Image.Image, *, max_degrees: float = DEFAULT_DESKEW_MAX_DEGREES) -> float | None:
    """Tilt of the text lines in degrees (positive = running down to the right), or None if unmeasurable."""
    import cv2

    small = _working_copy(image)
    ink = _ink_mask(small).astype(np.uint8) * 255
    height, width = ink.shape

    # Smear each text line into a solid bar so its edges are long straight segments.
    kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (max(9, width // 40), 1))
    bars = cv2.morphologyEx(ink, cv2.MORPH_CLOSE, kernel)
    edges = cv2.Canny(bars, 50, 150)

    min_length = max(40, width // 6)
    segments = cv2.HoughLinesP(
        edges,
        rho=1,
        # Half a degree per bin: the angle is taken from each segment's end points, not from its bin,
        # so finer bins cost time (4x at 0.125 degree) without making the median more exact.
        theta=np.pi / 360,
        threshold=max(30, min_length // 2),
        minLineLength=min_length,
        maxLineGap=max(5, width // 80),
    )
    if segments is None:
        return None

    angles: list[float] = []
    weights: list[float] = []
    for x1, y1, x2, y2 in segments[:, 0, :]:
        dx, dy = float(x2 - x1), float(y2 - y1)
        if dx < 0:
            dx, dy = -dx, -dy
        angle = math.degrees(math.atan2(dy, dx))
        if abs(angle) <= max_degrees:
            angles.append(angle)
            weights.append(math.hypot(dx, dy))

    if len(angles) < 3:
        return None

    return round(_weighted_median(angles, weights), 3)


def rotate_fine(image: Image.Image, skew_degrees: float) -> Image.Image:
    """Undoes a tilt of ``skew_degrees``, growing the canvas so no corner of the page is cut off."""
    import cv2

    source = np.asarray(image.convert("RGB"))
    height, width = source.shape[:2]
    centre = (width / 2.0, height / 2.0)

    # OpenCV's positive angle turns counterclockwise on screen, which lifts a line that runs down to the
    # right: exactly the correction of a positive skew.
    matrix = cv2.getRotationMatrix2D(centre, skew_degrees, 1.0)
    cos, sin = abs(matrix[0, 0]), abs(matrix[0, 1])
    new_width = int(math.ceil(height * sin + width * cos))
    new_height = int(math.ceil(height * cos + width * sin))
    matrix[0, 2] += new_width / 2.0 - centre[0]
    matrix[1, 2] += new_height / 2.0 - centre[1]

    straightened = cv2.warpAffine(
        source,
        matrix,
        (new_width, new_height),
        flags=cv2.INTER_CUBIC,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=WHITE,
    )
    return Image.fromarray(straightened)


def _working_copy(image: Image.Image) -> np.ndarray:
    gray = image.convert("L")
    longest = max(gray.size)
    if longest > ANALYSIS_MAX_SIDE:
        ratio = ANALYSIS_MAX_SIDE / longest
        gray = gray.resize(
            (max(1, round(gray.width * ratio)), max(1, round(gray.height * ratio))), Image.Resampling.BILINEAR
        )
    return np.asarray(gray)


def _ink_mask(gray: np.ndarray) -> np.ndarray:
    """True where there is ink. Otsu splits ink from paper; light watermarks and tints stay paper."""
    import cv2

    threshold, mask = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY_INV + cv2.THRESH_OTSU)
    # A blank or nearly uniform page makes Otsu split the paper noise; that is not ink.
    if threshold > 245 or float(gray.std()) < 4.0:
        return np.zeros(gray.shape, dtype=bool)

    # Long rules (table borders, underlines, card frames) are not text: they would add banding on
    # both axes and ink above, below and at both ends of every line. An opening with a long thin
    # kernel keeps exactly the straight runs no text stroke has, and those are removed.
    height, width = mask.shape
    horizontal = cv2.morphologyEx(mask, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_RECT, (max(20, width // 8), 1)))
    vertical = cv2.morphologyEx(mask, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_RECT, (1, max(20, height // 8))))
    rules = cv2.dilate(cv2.bitwise_or(horizontal, vertical), np.ones((3, 3), np.uint8))

    ink = (mask > 0) & (rules == 0)
    if float(ink.mean()) > MAX_INK_SHARE:
        return np.zeros(gray.shape, dtype=bool)
    return ink


def _profile_variance(ink: np.ndarray) -> float:
    """Variance of the ink fraction per row, normalised by the mean so both axes are comparable."""
    profile = ink.mean(axis=1)
    mean = float(profile.mean())
    if mean == 0.0:
        return 0.0
    return float(profile.var()) / (mean * mean)


def _tilted_profile_variance(ink: np.ndarray) -> float:
    """Best profile variance over a sweep of small tilts.

    A tilted page smears its rows: at 10 degrees a short card already bands less on its true axis
    than on the other one. Taking the best variance over tilts up to the deskew range compares the two
    axes as if each were straight. The sweep runs on a quarter-size mask, where it costs a few ms.
    """
    import cv2

    small = ink[::2, ::2].astype(np.uint8)
    height, width = small.shape
    centre = (width / 2.0, height / 2.0)
    best = _profile_variance(small > 0)
    for angle in AXIS_TILT_SWEEP:
        matrix = cv2.getRotationMatrix2D(centre, angle, 1.0)
        turned = cv2.warpAffine(small, matrix, (width, height), flags=cv2.INTER_NEAREST, borderValue=0)
        best = max(best, _profile_variance(turned > 0))
    return best


def _text_lines(ink: np.ndarray) -> list[tuple[int, int]]:
    """Row ranges [start, end) of the text lines, from the gaps of the horizontal profile."""
    profile = ink.mean(axis=1)
    if profile.max() == 0:
        return []

    active = profile > max(0.002, float(profile.max()) * 0.05)
    lines: list[tuple[int, int]] = []
    start: int | None = None
    for row, on in enumerate(active):
        if on and start is None:
            start = row
        elif not on and start is not None:
            lines.append((start, row))
            start = None
    if start is not None:
        lines.append((start, len(active)))

    # Lines shorter than 5 rows are specks or rules; they carry no shape to read.
    return [(top, bottom) for top, bottom in lines if bottom - top >= 5]


def _ascender_cue(ink: np.ndarray, lines: list[tuple[int, int]]) -> float | None:
    """(ink above the x-height band - ink below it) / both, over the lines that have such a band.

    Only lines that show an x-height structure vote: a core (rows at half the line's peak ink or more)
    clearly shorter than the line, with a real share of ink outside it. A line of capitals or digits
    has neither, and after a rotation its blurred edges or a stray rule would otherwise vote with an
    arbitrary sign (a card in capitals read -0.75 that way). None when too few lines qualify.
    """
    above = below = 0.0
    qualifying = 0
    for top, bottom in lines:
        band = ink[top:bottom].mean(axis=1)
        peak = float(band.max())
        if peak == 0.0:
            continue
        core = np.flatnonzero(band >= peak * 0.5)
        core_top, core_bottom = top + int(core[0]), top + int(core[-1]) + 1
        line_above = float(ink[top:core_top].sum())
        line_below = float(ink[core_bottom:bottom].sum())
        line_total = float(ink[top:bottom].sum())
        if (core_bottom - core_top) > X_HEIGHT_MAX_SHARE * (bottom - top):
            continue
        if line_total == 0.0 or (line_above + line_below) < TAIL_MIN_SHARE * line_total:
            continue
        qualifying += 1
        above += line_above
        below += line_below

    if qualifying < max(MIN_TEXT_LINES, ASCENDER_MIN_LINE_SHARE * len(lines)):
        return None
    return (above - below) / (above + below)


def _alignment_cue(ink: np.ndarray, lines: list[tuple[int, int]]) -> float | None:
    """(spread of right line ends - spread of left starts) / both; positive for a left-aligned page."""
    if len(lines) < 3:
        return None

    starts: list[int] = []
    ends: list[int] = []
    for top, bottom in lines:
        columns = np.flatnonzero(ink[top:bottom].any(axis=0))
        if columns.size:
            starts.append(int(columns[0]))
            ends.append(int(columns[-1]))

    if len(starts) < 3:
        return None

    left = _spread(starts)
    right = _spread(ends)
    if left + right < 1.0:
        return None
    # Raggedness only counts at the scale of the layout: a table whose rows end within a few pixels of
    # each other and start within 14 px would otherwise read as perfectly "right-aligned" (-1.0).
    return (right - left) / max(left + right, ALIGNMENT_MIN_SPREAD * ink.shape[1])


def _spread(values: list[int]) -> float:
    """Median absolute deviation: robust to the odd centred heading among left-aligned lines."""
    array = np.asarray(values, dtype=float)
    return float(np.median(np.abs(array - np.median(array))))


def _weighted_median(values: list[float], weights: list[float]) -> float:
    order = np.argsort(values)
    sorted_values = np.asarray(values)[order]
    cumulative = np.cumsum(np.asarray(weights)[order])
    index = int(np.searchsorted(cumulative, cumulative[-1] / 2.0))
    return float(sorted_values[min(index, len(sorted_values) - 1)])
