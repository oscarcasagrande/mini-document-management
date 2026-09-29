"""Gera os PDFs sintéticos que exercitam a camada de texto nativa do RF-009.

As amostras de `samples/synthetic/documents` são PDFs "escaneados" (só imagem): servem para provar que
um PDF sem texto embutido continua indo para o OCR, mas nada exercita o caminho contrário. Aqui:

- `pdf-texto-nativo.pdf`: uma página com texto de verdade (rótulos, valores, parágrafo, acentos),
  que o ocr-service deve ler do próprio PDF, sem rasterizar nem chamar o PaddleOCR;
- `pdf-misto.pdf`: página 1 com texto nativo, página 2 só com a imagem do cartão de CPF sintético
  **deitada** (90 graus): uma página vai pela camada nativa, a outra por OCR com correção de rotação.

Tudo fictício, como em `make-ocr-samples.py`: CPF de exemplo com DV válido, nomes inexistentes e a
marca AMOSTRA SINTETICA em diagonal (texto girado, que a leitura nativa descarta como não horizontal).

    docker run --rm -v "$PWD:/w" -w /w python:3.12-slim bash -c \
      "apt-get update -qq && apt-get install -y -qq fonts-dejavu-core >/dev/null \
       && pip install -q reportlab==5.0.1 pillow && python scripts/make-native-pdf-samples.py"

Saída: samples/synthetic/ocr/ (o cartão deitado é lido de lá; rode make-ocr-samples.py antes).
"""

from __future__ import annotations

import io
from pathlib import Path

from PIL import Image
from reportlab.lib.pagesizes import A4
from reportlab.lib.utils import ImageReader
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas

OUTPUT_DIR = Path(__file__).resolve().parent.parent / "samples" / "synthetic" / "ocr"
FONT_DIR = Path("/usr/share/fonts/truetype/dejavu")
MARK_TEXT = "AMOSTRA SINTETICA - SEM VALOR LEGAL"

FIELDS = [
    ("NOME", "MARIA APARECIDA DA SILVA SOUZA"),
    ("CPF", "111.444.777-35"),
    ("DATA DE NASCIMENTO", "14/03/1985"),
    ("ENDEREÇO", "Avenida dos Testes, 250, apto 71 - São Paulo/SP"),
    ("CEP", "04000-000"),
]

PARAGRAPH = [
    "Declaramos, para os devidos fins, que a pessoa acima identificada mantém contrato de prestação",
    "de serviços de leitura documental com a EXEMPLO SINTÉTICO LTDA, inscrita no CNPJ sob o número",
    "12.ABC.345/01DE-35, vigente desde 02 de setembro de 2025, sem pendências até a presente data.",
    "Este documento foi gerado eletronicamente e contém camada de texto pesquisável.",
]


def register_fonts() -> None:
    pdfmetrics.registerFont(TTFont("DejaVu", str(FONT_DIR / "DejaVuSans.ttf")))
    pdfmetrics.registerFont(TTFont("DejaVu-Bold", str(FONT_DIR / "DejaVuSans-Bold.ttf")))


def watermark(pdf: canvas.Canvas) -> None:
    width, height = A4
    pdf.saveState()
    pdf.setFillColorRGB(0.85, 0.35, 0.35, alpha=0.25)
    pdf.setFont("DejaVu-Bold", 30)
    pdf.translate(width / 2, height / 2)
    pdf.rotate(28)
    pdf.drawCentredString(0, 0, MARK_TEXT)
    pdf.restoreState()


def native_page(pdf: canvas.Canvas) -> None:
    width, height = A4
    pdf.setFont("DejaVu-Bold", 16)
    pdf.drawCentredString(width / 2, height - 90, "DECLARAÇÃO DE PRESTAÇÃO DE SERVIÇOS")
    pdf.setFont("DejaVu", 10)
    pdf.drawCentredString(width / 2, height - 108, "EXEMPLO SINTÉTICO LTDA - CNPJ 12.ABC.345/01DE-35")

    y = height - 160
    for label, value in FIELDS:
        pdf.setFont("DejaVu", 9)
        pdf.drawString(72, y, label)
        pdf.setFont("DejaVu-Bold", 12)
        pdf.drawString(72, y - 16, value)
        y -= 44

    pdf.setFont("DejaVu", 11)
    y -= 10
    for line in PARAGRAPH:
        pdf.drawString(72, y, line)
        y -= 17

    pdf.setFont("DejaVu", 10)
    pdf.drawString(72, 120, "São Paulo, 24 de setembro de 2026.")
    pdf.drawRightString(width - 72, 60, "página 1")
    watermark(pdf)
    pdf.showPage()


def scanned_sideways_page(pdf: canvas.Canvas, card: Path) -> None:
    """Uma página só de imagem: o cartão de CPF deitado, como sai de um scanner com o papel virado."""
    width, height = A4
    with Image.open(card) as image:
        sideways = image.convert("RGB").rotate(90, expand=True)
    buffer = io.BytesIO()
    sideways.save(buffer, format="PNG")
    buffer.seek(0)

    target_height = height - 144
    target_width = target_height * sideways.width / sideways.height
    pdf.drawImage(ImageReader(buffer), (width - target_width) / 2, 72, target_width, target_height)
    pdf.showPage()


def main() -> None:
    register_fonts()
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    single = OUTPUT_DIR / "pdf-texto-nativo.pdf"
    pdf = canvas.Canvas(str(single), pagesize=A4, invariant=1)
    native_page(pdf)
    pdf.save()

    mixed = OUTPUT_DIR / "pdf-misto.pdf"
    pdf = canvas.Canvas(str(mixed), pagesize=A4, invariant=1)
    native_page(pdf)
    scanned_sideways_page(pdf, OUTPUT_DIR / "cpf-card-limpo.png")
    pdf.save()

    for path in (single, mixed):
        print(f"{path.name:<24} {path.stat().st_size:>9} bytes")


if __name__ == "__main__":
    main()
