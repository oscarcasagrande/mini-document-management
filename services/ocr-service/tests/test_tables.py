"""The table-likelihood heuristic on hand-built block lists: no image, no engine."""

from app.engine import RecognizedBlock
from app.tables import assess_table


def block(text: str, left: float, top: float, width: float, height: float = 30.0, confidence: float = 0.6) -> RecognizedBlock:
    right, bottom = left + width, top + height
    return RecognizedBlock(text, confidence, [left, top, right, top, right, bottom, left, bottom])


def grid(rows: int, columns: int, confidence: float, *, right_aligned_from: int | None = None) -> list[RecognizedBlock]:
    blocks = []
    for row in range(rows):
        for column in range(columns):
            width = 60 + (row * 13 + column * 7) % 50
            if right_aligned_from is not None and column >= right_aligned_from:
                left = 200 + column * 250 + 150 - width
            else:
                left = 200 + column * 250
            blocks.append(block(f"r{row}c{column}", left, 100 + row * 70, width, confidence=confidence))
    return blocks


def test_a_low_confidence_four_by_four_grid_triggers():
    assessment = assess_table(grid(4, 4, confidence=0.55))

    assert assessment.table_like is True
    assert assessment.grid_rows == 4
    assert assessment.columns == 4
    assert assessment.grid_confidence == 0.55
    assert assessment.triggers(0.80) is True


def test_a_high_confidence_grid_is_a_table_but_does_not_trigger():
    assessment = assess_table(grid(4, 4, confidence=0.98))

    assert assessment.table_like is True
    assert assessment.triggers(0.80) is False


def test_a_single_column_of_paragraph_lines_is_not_a_table():
    lines = [block(f"line {index}", 150, 100 + index * 46, 1200 - (index % 3) * 90, height=36) for index in range(30)]

    assessment = assess_table(lines)

    assert assessment.table_like is False
    assert assessment.triggers(0.80) is False


def test_right_aligned_figure_columns_count_as_columns():
    # Two left-aligned columns and three right-aligned ones, like an invoice.
    assessment = assess_table(grid(6, 5, confidence=0.5, right_aligned_from=2))

    assert assessment.table_like is True
    assert assessment.columns >= 3
    assert assessment.triggers(0.80) is True


def test_three_unrelated_short_lines_are_not_a_table():
    blocks = [block("TITULO", 500, 80, 300), block("data", 900, 200, 120), block("assinatura", 150, 900, 250)]

    assert assess_table(blocks).table_like is False


def test_a_two_column_label_value_form_is_not_a_table():
    blocks = []
    for row in range(8):
        blocks.append(block("ROTULO", 100, 100 + row * 80, 140))
        blocks.append(block("valor lido", 400, 100 + row * 80, 300))

    assert assess_table(blocks).table_like is False


def test_rows_that_do_not_line_up_in_columns_are_not_a_grid():
    # Three blocks per row, but at different places on every row: prose split by the detector.
    blocks = []
    for row in range(6):
        for column in range(3):
            blocks.append(block("palavras", 100 + row * 97 + column * 400 + (column * row * 61) % 180, 100 + row * 60, 90))

    assert assess_table(blocks).table_like is False


def test_blocks_without_coordinates_or_text_are_ignored():
    blocks = grid(4, 4, confidence=0.5)
    blocks += [RecognizedBlock("sem caixa", 0.1, []), RecognizedBlock("   ", 0.1, [0, 0, 10, 0, 10, 10, 0, 10])]

    assessment = assess_table(blocks)

    assert assessment.table_like is True
    assert assessment.grid_confidence == 0.5


def test_an_empty_page_is_not_a_table():
    assessment = assess_table([])

    assert assessment.table_like is False
    assert assessment.grid_confidence is None
