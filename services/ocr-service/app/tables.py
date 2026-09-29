"""Does a page look like a table? Decided from the PP-OCRv5 block layout alone (RF-009).

Pure Python over the block list, no image and no model: it runs on every page and must cost nothing,
because PP-StructureV3 (tens of seconds per page) is only worth calling when this says yes.

A table here is a grid, not just a few short lines:

1. Blocks are grouped into **rows** by the vertical centre: a block joins a row when its centre is
   within ``ROW_TOLERANCE`` median block heights of the row's centre. The tolerance is tighter than
   "1.5 heights": the lines of a dense paragraph are ~1.2-1.5 box heights apart and would merge.
2. Rows with at least ``MIN_CELLS_PER_ROW`` blocks are candidate **grid rows**.
3. Their blocks' horizontal anchors are binned into **columns** (bin width = one median block height).
   A column counts when at least ``MIN_ROWS`` different grid rows use it. Left, right and centre
   anchors are binned separately and the best of the three is taken, because figures in a table are
   usually right-aligned and titles centred: counting starts only would miss exactly the numeric
   columns that make a table a table.
4. The page is table-like when at least ``MIN_ROWS`` grid rows each have ``MIN_CELLS_PER_ROW``
   blocks sitting in counted columns, and at least ``MIN_COLUMNS`` columns are counted.

The trigger then asks for low confidence too: a grid PP-OCRv5 already read well (a clean ID card with
fields side by side is a grid) gains nothing from a second, slower engine.
"""

from __future__ import annotations

import statistics
from collections.abc import Callable, Sequence
from dataclasses import dataclass

from .engine import RecognizedBlock

ROW_TOLERANCE = 0.6
MIN_ROWS = 3
MIN_CELLS_PER_ROW = 3
MIN_COLUMNS = 3


@dataclass(frozen=True)
class TableAssessment:
    table_like: bool
    grid_rows: int
    columns: int
    # Mean confidence of the blocks that form the grid; None when there is no grid.
    grid_confidence: float | None

    def triggers(self, confidence_threshold: float) -> bool:
        """Table-like and read with less confidence than the threshold."""
        return self.table_like and self.grid_confidence is not None and self.grid_confidence < confidence_threshold


@dataclass(frozen=True)
class _Box:
    left: float
    right: float
    centre_y: float
    height: float
    confidence: float | None


NO_TABLE = TableAssessment(False, 0, 0, None)


def assess_table(blocks: Sequence[RecognizedBlock]) -> TableAssessment:
    boxes = [box for box in (_box(block) for block in blocks) if box is not None]
    if len(boxes) < MIN_ROWS * MIN_CELLS_PER_ROW:
        return NO_TABLE

    unit = statistics.median(box.height for box in boxes)
    if unit <= 0:
        return NO_TABLE

    rows = [row for row in _rows(boxes, unit * ROW_TOLERANCE) if len(row) >= MIN_CELLS_PER_ROW]
    if len(rows) < MIN_ROWS:
        return NO_TABLE

    best: tuple[int, int, list[_Box]] = (0, 0, [])
    for anchor in (_left, _right, _centre):
        column_of = _columns(rows, anchor, unit)
        grid_rows = [row for row in rows if sum(1 for box in row if id(box) in column_of) >= MIN_CELLS_PER_ROW]
        cells = [box for row in grid_rows for box in row if id(box) in column_of]
        columns = len({column_of[id(box)] for box in cells})
        if (len(grid_rows), columns) > best[:2]:
            best = (len(grid_rows), columns, cells)

    grid_rows, column_count, cells = best
    if grid_rows < MIN_ROWS or column_count < MIN_COLUMNS:
        return TableAssessment(False, grid_rows, column_count, None)

    confidences = [b.confidence for b in cells if b.confidence is not None]
    confidence = round(sum(confidences) / len(confidences), 4) if confidences else None
    return TableAssessment(True, grid_rows, column_count, confidence)


def _box(block: RecognizedBlock) -> _Box | None:
    coordinates = block.bounding_box
    if len(coordinates) < 4 or not block.text.strip():
        return None

    xs = coordinates[0::2]
    ys = coordinates[1::2]
    height = max(ys) - min(ys)
    if height <= 0:
        return None
    return _Box(min(xs), max(xs), (min(ys) + max(ys)) / 2, height, block.confidence)


def _rows(boxes: list[_Box], tolerance: float) -> list[list[_Box]]:
    rows: list[list[_Box]] = []
    centres: list[float] = []
    for box in sorted(boxes, key=lambda b: b.centre_y):
        if rows and abs(box.centre_y - centres[-1]) <= tolerance:
            rows[-1].append(box)
            centres[-1] = sum(b.centre_y for b in rows[-1]) / len(rows[-1])
        else:
            rows.append([box])
            centres.append(box.centre_y)
    return rows


def _left(box: _Box) -> float:
    return box.left


def _right(box: _Box) -> float:
    return box.right


def _centre(box: _Box) -> float:
    return (box.left + box.right) / 2


def _columns(rows: list[list[_Box]], anchor: Callable[[_Box], float], unit: float) -> dict[int, int]:
    """Maps id(box) to its column, for the boxes in columns that at least MIN_ROWS rows use.

    Anchors are sorted and cut wherever two neighbours are more than one block height apart, so a
    column is a run of anchors that line up within that tolerance.
    """
    anchored = sorted(((anchor(box), index, box) for index, row in enumerate(rows) for box in row), key=lambda item: item[0])

    column_of: dict[int, int] = {}
    cluster: list[tuple[float, int, _Box]] = []
    column = 0

    def close(members: list[tuple[float, int, _Box]]) -> None:
        nonlocal column
        if len({index for _, index, _ in members}) >= MIN_ROWS:
            for _, _, box in members:
                column_of[id(box)] = column
            column += 1

    for item in anchored:
        if cluster and item[0] - cluster[-1][0] > unit:
            close(cluster)
            cluster = []
        cluster.append(item)
    if cluster:
        close(cluster)

    return column_of
