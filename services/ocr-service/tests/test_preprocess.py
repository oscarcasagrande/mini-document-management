"""Orientation correction in isolation: no engine, no API, only images.

The two acceptance cases of RF-009 are here: a page turned 90 degrees comes back upright, and a page
tilted 10 degrees comes back straight. The pages are drawn by the tests (``images.py``); the real
synthetic CPF card of ``samples/synthetic/ocr`` is checked too when the whole repository is mounted.
"""

import numpy as np
import pytest
from PIL import Image, ImageDraw, ImageFont

from app.preprocess import correct_orientation, detect_skew

from .images import REPOSITORY_CARD, WHITE, cpf_card

PARAGRAPH = [
    "Pelo presente instrumento particular, de um lado a empresa Exemplo Sintetico Ltda,",
    "inscrita no cadastro sob o numero indicado, com sede na Rua das Amostras, doravante",
    "denominada contratada, e de outro lado a pessoa fisica identificada no quadro abaixo,",
    "tem entre si justo e contratado o quanto segue, observadas as clausulas seguintes.",
    "Clausula primeira: o objeto e a prestacao de servicos de leitura de documentos.",
    "Clausula segunda: o prazo de vigencia e de doze meses contados da assinatura,",
    "renovavel por igual periodo mediante manifestacao expressa de ambas as partes.",
    "Clausula terceira: pela prestacao dos servicos sera pago o valor mensal ajustado.",
    "Clausula quarta: as partes observam a lei de protecao de dados pessoais em vigor.",
]


def card() -> Image.Image:
    return cpf_card()


def text_page() -> Image.Image:
    """Left-aligned mixed-case paragraph: both direction cues have something to say."""
    font = ImageFont.load_default(size=26)
    image = Image.new("RGB", (1240, 900), WHITE)
    draw = ImageDraw.Draw(image)
    for index, line in enumerate(PARAGRAPH):
        draw.text((80, 80 + index * 44), line, font=font, fill=(10, 10, 10))
    return image


def caps_table() -> Image.Image:
    """All capitals and right-aligned figures: no ascenders, and line ends aligned better than starts."""
    font = ImageFont.load_default(size=26)
    image = Image.new("RGB", (1400, 1050), WHITE)
    draw = ImageDraw.Draw(image)
    draw.text((100, 60), "DEMONSTRATIVO DE SERVICOS PRESTADOS", font=ImageFont.load_default(size=32), fill=(0, 0, 0))
    names = ["DIGITALIZACAO A4", "RECONHECIMENTO", "EXTRACAO DE CAMPOS", "VALIDACAO"]
    for row in range(12):
        y = 160 + row * 64
        draw.text((100, y), f"{row + 1:03d}", font=font, fill=(0, 0, 0))
        draw.text((220, y), names[row % 4], font=font, fill=(0, 0, 0))
        draw.text((900, y), f"{(row + 3) * 137}", font=font, fill=(0, 0, 0), anchor="ra")
        draw.text((1300, y), f"{(row + 1) * 211},00", font=font, fill=(0, 0, 0), anchor="ra")
        draw.line([(90, y + 48), (1310, y + 48)], fill=(0, 0, 0), width=2)
    return image


def tilt(image: Image.Image, degrees: float) -> Image.Image:
    """Counterclockwise on screen, as a page fed crooked into a scanner."""
    return image.rotate(degrees, resample=Image.Resampling.BICUBIC, expand=True, fillcolor=WHITE)


def test_an_upright_page_is_returned_untouched():
    original = card()

    result = correct_orientation(original)

    assert result.rotation_degrees == 0
    assert result.deskewed is False
    assert np.array_equal(np.asarray(result.image), np.asarray(original))


@pytest.mark.parametrize(("turned_counterclockwise", "expected_correction"), [(90, 90), (270, 270)])
def test_a_card_turned_sideways_comes_back_upright(turned_counterclockwise, expected_correction):
    original = card()

    result = correct_orientation(original.rotate(turned_counterclockwise, expand=True))

    # The correction is reported clockwise; turning the sideways card that far restores it exactly.
    assert result.rotation_degrees == expected_correction
    assert result.deskewed is False
    assert result.image.size == original.size
    assert np.array_equal(np.asarray(result.image), np.asarray(original))


def test_an_upside_down_paragraph_is_flipped():
    original = text_page()

    result = correct_orientation(original.rotate(180))

    assert result.rotation_degrees == 180
    assert np.array_equal(np.asarray(result.image), np.asarray(original))


@pytest.mark.parametrize("page", [card, text_page, caps_table], ids=["card", "paragraph", "caps-table"])
def test_no_upright_page_is_ever_flipped(page):
    """Flipping a correct page is the worst outcome; a page without direction cues is left alone."""
    for degrees in (0.0, 3.0, -3.0):
        image = tilt(page(), degrees) if degrees else page()
        assert correct_orientation(image).rotation_degrees == 0


def test_a_page_tilted_ten_degrees_is_deskewed():
    tilted = tilt(card(), 10.0)

    result = correct_orientation(tilted)

    assert result.rotation_degrees == 0
    assert result.deskewed is True
    # PIL's counterclockwise turn makes the text run up to the right: a negative skew.
    assert result.skew_degrees == pytest.approx(-10.0, abs=0.3)
    residual = detect_skew(result.image)
    assert residual is not None and abs(residual) < 0.3
    # A second pass finds nothing left to straighten.
    assert correct_orientation(result.image).deskewed is False


def test_a_clockwise_tilt_is_measured_positive_and_undone():
    result = correct_orientation(tilt(text_page(), -6.0))

    assert result.deskewed is True
    assert result.skew_degrees == pytest.approx(6.0, abs=0.3)
    assert abs(detect_skew(result.image)) < 0.3


def test_a_tilt_below_the_floor_is_left_alone():
    result = correct_orientation(tilt(card(), 0.2))

    assert result.deskewed is False


def test_a_tilt_above_the_cap_is_not_corrected():
    result = correct_orientation(tilt(text_page(), 8.0), deskew_max_degrees=5.0)

    assert result.deskewed is False


def test_a_sideways_page_that_is_also_tilted_is_turned_and_straightened():
    original = card()

    result = correct_orientation(tilt(original, 10.0).rotate(90, expand=True))

    assert result.rotation_degrees == 90
    assert result.deskewed is True
    assert abs(detect_skew(result.image)) < 0.3
    assert result.image.width > result.image.height


def test_a_blank_page_is_neither_turned_nor_deskewed():
    result = correct_orientation(Image.new("RGB", (800, 1100), WHITE))

    assert result.rotation_degrees == 0
    assert result.deskewed is False
    assert result.skew_degrees is None


def test_both_corrections_can_be_switched_off():
    sideways = card().rotate(90, expand=True)

    result = correct_orientation(tilt(sideways, 5.0), detect_rotation=False, deskew=False)

    assert result.rotation_degrees == 0
    assert result.deskewed is False


@pytest.mark.skipif(not REPOSITORY_CARD.exists(), reason="needs the repository's samples/synthetic mounted")
def test_the_synthetic_cpf_card_sample_is_turned_upright_and_deskewed():
    with Image.open(REPOSITORY_CARD) as image:
        original = image.convert("RGB")

    turned = correct_orientation(original.rotate(90, expand=True))
    straightened = correct_orientation(tilt(original, 10.0))

    assert turned.rotation_degrees == 90
    assert np.array_equal(np.asarray(turned.image), np.asarray(original))
    assert straightened.deskewed is True
    assert abs(detect_skew(straightened.image)) < 0.3
