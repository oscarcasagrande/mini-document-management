"""PDFs built for the tests with reportlab: a real text layer, or only an image, or both."""

from __future__ import annotations

import io

from PIL import Image, ImageDraw
from reportlab.lib.pagesizes import A4
from reportlab.lib.utils import ImageReader
from reportlab.pdfgen import canvas

FIELDS = [
    ("NOME", "MARIA APARECIDA DA SILVA SOUZA"),
    ("CPF", "111.444.777-35"),
    ("DATA DE NASCIMENTO", "14/03/1985"),
]


def text_page(pdf: canvas.Canvas, *, watermark: bool = True) -> None:
    """Labels at x = 72 pt with values below them, a paragraph, and a diagonal watermark."""
    width, height = A4
    pdf.setFont("Helvetica-Bold", 16)
    pdf.drawString(72, height - 90, "DECLARACAO DE PRESTACAO DE SERVICOS")
    y = height - 150
    for label, value in FIELDS:
        pdf.setFont("Helvetica", 9)
        pdf.drawString(72, y, label)
        pdf.setFont("Helvetica-Bold", 12)
        pdf.drawString(72, y - 16, value)
        # A second column on the same line: the native reader must split it like the OCR detector does.
        pdf.setFont("Helvetica", 9)
        pdf.drawString(360, y - 16, "CAMPO DA DIREITA")
        y -= 44
    pdf.setFont("Helvetica", 11)
    pdf.drawString(72, y - 20, "Declaramos para os devidos fins que a pessoa acima mantem contrato vigente.")
    if watermark:
        pdf.saveState()
        pdf.translate(width / 2, height / 2)
        pdf.rotate(28)
        pdf.setFont("Helvetica-Bold", 30)
        pdf.drawCentredString(0, 0, "AMOSTRA SINTETICA - SEM VALOR LEGAL")
        pdf.restoreState()
    pdf.showPage()


def image_page(pdf: canvas.Canvas, *, stamp: str | None = None) -> None:
    """A page that is only a picture of text (a scan), optionally with a short digital stamp over it."""
    picture = Image.new("RGB", (1200, 1700), (250, 250, 248))
    draw = ImageDraw.Draw(picture)
    for index in range(20):
        draw.text((100, 100 + index * 70), f"LINHA DIGITALIZADA NUMERO {index}", fill=(0, 0, 0))
    buffer = io.BytesIO()
    picture.save(buffer, format="PNG")
    buffer.seek(0)

    width, height = A4
    pdf.drawImage(ImageReader(buffer), 0, 0, width, height)
    if stamp:
        pdf.setFont("Helvetica", 8)
        pdf.drawString(40, 20, stamp)
    pdf.showPage()


def build(*pages: str, rotate: int = 0) -> bytes:
    """``pages`` of "text", "image", "stamped" (image + short text), "empty" or "number" (a page number only)."""
    buffer = io.BytesIO()
    pdf = canvas.Canvas(buffer, pagesize=A4, invariant=1)
    if rotate:
        pdf.setPageRotation(rotate)
    for kind in pages:
        if kind == "text":
            text_page(pdf)
        elif kind == "image":
            image_page(pdf)
        elif kind == "stamped":
            image_page(pdf, stamp="Documento assinado digitalmente por EXEMPLO em 24/09/2026 codigo 1A2B3C")
        elif kind == "number":
            pdf.setFont("Helvetica", 9)
            pdf.drawString(500, 30, "pagina 3")
            pdf.showPage()
        else:
            pdf.showPage()
    pdf.save()
    return buffer.getvalue()
