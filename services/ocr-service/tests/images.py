"""Page images drawn by the tests with Pillow's built-in font, so no system font and no file is needed.

The repository's .gitignore keeps every image out of version control except the synthetic samples, as a
guard against committing a real document; the tests therefore draw what they need.
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

WHITE = (255, 255, 255)


def _repository_card() -> Path:
    """The synthetic CPF card of samples/synthetic/ocr; a path that does not exist when only the
    service directory is mounted (as in the usual container test run)."""
    here = Path(__file__).resolve()
    root = here.parents[3] if len(here.parents) > 3 else here.parent
    return root / "samples" / "synthetic" / "ocr" / "cpf-card-limpo.png"


REPOSITORY_CARD = _repository_card()


def cpf_card() -> Image.Image:
    """Same layout as scripts/make-ocr-samples.py: centred all-caps headers, left-aligned labels and values."""
    image = Image.new("RGB", (1100, 700), (247, 245, 238))
    draw = ImageDraw.Draw(image)
    draw.rectangle([(0, 0), (1099, 699)], outline=(120, 120, 120), width=3)
    draw.text((550, 70), "REPUBLICA FEDERATIVA DO BRASIL", font=ImageFont.load_default(size=30), fill=(30, 30, 30), anchor="mm")
    draw.text((550, 112), "MINISTERIO DA FAZENDA", font=ImageFont.load_default(size=24), fill=(60, 60, 60), anchor="mm")
    draw.text((550, 148), "SECRETARIA DA RECEITA FEDERAL", font=ImageFont.load_default(size=24), fill=(60, 60, 60), anchor="mm")
    draw.line([(90, 180), (1010, 180)], fill=(120, 120, 120), width=2)
    draw.text((550, 218), "CADASTRO DE PESSOAS FISICAS", font=ImageFont.load_default(size=32), fill=(20, 20, 20), anchor="mm")
    label, value = ImageFont.load_default(size=22), ImageFont.load_default(size=40)
    for top, name, content in ((300, "NUMERO DE INSCRICAO", "111.444.777-35"), (420, "NOME", "MARIA APARECIDA DA SILVA SOUZA"), (540, "NASCIMENTO", "14/03/1985")):
        draw.text((110, top), name, font=label, fill=(90, 90, 90))
        draw.text((110, top + 32), content, font=value, fill=(10, 10, 10))
    draw.text((990, 600), "INSCRICAO EM 02/09/2003", font=ImageFont.load_default(size=20), fill=(110, 110, 110), anchor="rs")
    return image
