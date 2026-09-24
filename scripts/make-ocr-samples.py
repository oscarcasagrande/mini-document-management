"""Gera as amostras sintéticas usadas para medir e testar o OCR.

As amostras da Etapa 1 servem para validar assinatura de arquivo e contagem de páginas, mas não
têm texto: medir OCR nelas produziria números sem significado. Este gerador produz páginas com
densidade de texto realista, em resolução de digitalização, além de um cartão de CPF sintético
para o primeiro tipo estruturado a ir de ponta a ponta.

Tudo aqui é fictício por construção: os CPFs têm dígito verificador válido mas são os exemplos
de manual, os nomes não existem e cada peça carrega uma marca de AMOSTRA SINTÉTICA.

    docker run --rm -v "$PWD:/w" -w /w python:3.12-slim bash -c \
      "apt-get update -qq && apt-get install -y -qq fonts-dejavu-core >/dev/null \
       && pip install -q pillow && python scripts/make-ocr-samples.py"

Saída: samples/synthetic/ocr/
"""

from __future__ import annotations

import random
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

OUTPUT_DIR = Path(__file__).resolve().parent.parent / "samples" / "synthetic" / "ocr"

FONT_DIR = Path("/usr/share/fonts/truetype/dejavu")
SANS = FONT_DIR / "DejaVuSans.ttf"
SANS_BOLD = FONT_DIR / "DejaVuSans-Bold.ttf"
MONO = FONT_DIR / "DejaVuSansMono.ttf"

# A4 a 200 DPI, que é a resolução típica de um scanner de mesa em modo documento.
A4_200DPI = (1654, 2339)

# CPFs de dígito verificador válido e origem obviamente didática.
CPF_PRIMARY = "111.444.777-35"
CPF_SECONDARY = "529.982.247-25"


def font(path: Path, size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(str(path), size)


MARK_TEXT = "AMOSTRA SINTETICA - SEM VALOR LEGAL"


def watermark_diagonal(image: Image.Image) -> None:
    """Carimbo diagonal para páginas cheias, onde sobra área livre entre os parágrafos."""
    layer = Image.new("RGBA", image.size, (255, 255, 255, 0))
    draw = ImageDraw.Draw(layer)
    mark = font(SANS_BOLD, max(18, image.width // 26))
    draw.text((image.width // 2, image.height // 2), MARK_TEXT, font=mark, fill=(200, 60, 60, 45), anchor="mm")
    rotated = layer.rotate(28, resample=Image.Resampling.BICUBIC)

    merged = Image.alpha_composite(image.convert("RGBA"), rotated).convert(image.mode)
    image.paste(merged)


def watermark_footer(image: Image.Image) -> None:
    """
    Carimbo em rodapé, usado no cartão de CPF: um carimbo diagonal cruzaria justamente o número, o
    nome e a data, que são os campos que o extrator precisa ler.
    """
    draw = ImageDraw.Draw(image)
    band_top = image.height - 58
    draw.rectangle([(24, band_top), (image.width - 25, image.height - 25)], fill=(232, 226, 218))
    draw.text(
        (image.width // 2, band_top + 16),
        MARK_TEXT,
        font=font(SANS_BOLD, 22),
        fill=(150, 60, 60),
        anchor="ma",
    )


def build_cpf_card() -> Image.Image:
    """Cartão de CPF sintético, com os três campos que o extrator precisa ler."""
    width, height = 1100, 700
    image = Image.new("RGB", (width, height), (247, 245, 238))
    draw = ImageDraw.Draw(image)

    draw.rectangle([(0, 0), (width - 1, height - 1)], outline=(120, 120, 120), width=3)
    draw.rectangle([(22, 22), (width - 23, height - 23)], outline=(170, 170, 170), width=1)

    draw.text((width // 2, 70), "REPUBLICA FEDERATIVA DO BRASIL", font=font(SANS_BOLD, 30), fill=(30, 30, 30), anchor="mm")
    draw.text((width // 2, 112), "MINISTERIO DA FAZENDA", font=font(SANS, 24), fill=(60, 60, 60), anchor="mm")
    draw.text((width // 2, 148), "SECRETARIA DA RECEITA FEDERAL", font=font(SANS, 24), fill=(60, 60, 60), anchor="mm")
    draw.line([(90, 180), (width - 90, 180)], fill=(120, 120, 120), width=2)
    draw.text((width // 2, 218), "CADASTRO DE PESSOAS FISICAS", font=font(SANS_BOLD, 32), fill=(20, 20, 20), anchor="mm")

    label = font(SANS, 22)
    value = font(SANS_BOLD, 40)
    value_mono = font(MONO, 44)

    draw.text((110, 300), "NUMERO DE INSCRICAO", font=label, fill=(90, 90, 90))
    draw.text((110, 332), CPF_PRIMARY, font=value_mono, fill=(10, 10, 10))

    draw.text((110, 420), "NOME", font=label, fill=(90, 90, 90))
    draw.text((110, 452), "MARIA APARECIDA DA SILVA SOUZA", font=value, fill=(10, 10, 10))

    draw.text((110, 540), "NASCIMENTO", font=label, fill=(90, 90, 90))
    draw.text((110, 572), "14/03/1985", font=value, fill=(10, 10, 10))

    draw.text((width - 110, 572), "INSCRICAO EM 02/09/2003", font=font(SANS, 20), fill=(110, 110, 110), anchor="rs")

    watermark_footer(image)
    return image


def scan_effect(image: Image.Image, *, angle: float, noise: int, blur: float) -> Image.Image:
    """Aproxima uma digitalização: leve rotação, ruído, borrão e conversão para cinza."""
    rotated = image.rotate(angle, resample=Image.Resampling.BICUBIC, expand=True, fillcolor=(255, 255, 255))
    gray = rotated.convert("L").filter(ImageFilter.GaussianBlur(blur))

    pixels = gray.load()
    rng = random.Random(20260924)
    for y in range(gray.height):
        for x in range(gray.width):
            if rng.random() < 0.06:
                delta = rng.randint(-noise, noise)
                pixels[x, y] = max(0, min(255, pixels[x, y] + delta))

    return gray


PARAGRAPHS = [
    "CONTRATO DE PRESTACAO DE SERVICOS DE LEITURA DOCUMENTAL",
    "",
    "Pelo presente instrumento particular, de um lado a empresa EXEMPLO SINTETICO LTDA, inscrita",
    "no CNPJ sob o numero 12.ABC.345/01DE-35, com sede na Rua das Amostras, numero 1000, bairro",
    "Centro, na cidade de Sao Paulo, Estado de Sao Paulo, CEP 01000-000, neste ato representada",
    "na forma de seu contrato social, doravante denominada CONTRATADA, e de outro lado a pessoa",
    "fisica MARIA APARECIDA DA SILVA SOUZA, portadora do CPF numero 111.444.777-35, residente e",
    "domiciliada na Avenida dos Testes, numero 250, apartamento 71, CEP 04000-000, doravante",
    "denominada CONTRATANTE, tem entre si justo e contratado o quanto segue.",
    "",
    "CLAUSULA PRIMEIRA - DO OBJETO",
    "O objeto do presente contrato e a prestacao de servicos de digitalizacao, reconhecimento",
    "optico de caracteres e extracao de dados estruturados de documentos fornecidos pela",
    "CONTRATANTE, observados os niveis de qualidade descritos no anexo tecnico.",
    "",
    "CLAUSULA SEGUNDA - DO PRAZO",
    "O prazo de vigencia e de doze meses contados da data de assinatura, renovavel por igual",
    "periodo mediante manifestacao expressa de ambas as partes com trinta dias de antecedencia.",
    "",
    "CLAUSULA TERCEIRA - DO PRECO E DA FORMA DE PAGAMENTO",
    "Pela prestacao dos servicos a CONTRATANTE pagara o valor mensal de R$ 4.780,00, com",
    "vencimento no quinto dia util do mes subsequente ao da prestacao, mediante apresentacao",
    "de nota fiscal de servicos eletronica.",
    "",
    "CLAUSULA QUARTA - DA PROTECAO DE DADOS",
    "As partes obrigam-se a observar a Lei numero 13.709 de 2018, tratando os dados pessoais",
    "exclusivamente para as finalidades previstas neste instrumento, pelo tempo necessario e",
    "com as medidas tecnicas e administrativas de seguranca adequadas.",
    "",
    "CLAUSULA QUINTA - DA RESCISAO",
    "O contrato podera ser rescindido por qualquer das partes, sem onus, mediante comunicacao",
    "escrita com antecedencia minima de trinta dias corridos.",
    "",
    "CLAUSULA SEXTA - DO FORO",
    "Fica eleito o foro da comarca de Sao Paulo, Estado de Sao Paulo, para dirimir quaisquer",
    "duvidas oriundas do presente contrato, com renuncia expressa a qualquer outro.",
    "",
    "Sao Paulo, 24 de setembro de 2026.",
]


def build_text_page() -> Image.Image:
    """Página A4 com densidade de texto de contrato, o pior caso comum de latência."""
    image = Image.new("RGB", A4_200DPI, (252, 252, 250))
    draw = ImageDraw.Draw(image)

    body = font(SANS, 30)
    heading = font(SANS_BOLD, 32)

    y = 200
    for line in PARAGRAPHS:
        if not line:
            y += 22
            continue
        is_heading = line.startswith("CLAUSULA") or line.startswith("CONTRATO")
        draw.text((150, y), line, font=heading if is_heading else body, fill=(15, 15, 15))
        y += 52 if is_heading else 46

    draw.text((A4_200DPI[0] - 150, A4_200DPI[1] - 140), "pagina 1 de 1", font=font(SANS, 26), fill=(120, 120, 120), anchor="rs")

    watermark_diagonal(image)
    return image


TABLE_HEADER = ["ITEM", "DESCRICAO DO SERVICO", "QTD", "VALOR UNIT", "VALOR TOTAL"]
TABLE_ROWS = [
    ["001", "Digitalizacao de pagina A4 monocromatica", "1.200", "0,18", "216,00"],
    ["002", "Reconhecimento optico de caracteres", "1.200", "0,42", "504,00"],
    ["003", "Extracao estruturada de campos", "860", "1,15", "989,00"],
    ["004", "Validacao de digito verificador", "860", "0,05", "43,00"],
    ["005", "Indexacao e armazenamento por 12 meses", "1.200", "0,09", "108,00"],
    ["006", "Consulta via interface web", "3.400", "0,02", "68,00"],
    ["007", "Consulta via API REST", "9.800", "0,02", "196,00"],
    ["008", "Reprocessamento sob demanda", "120", "0,60", "72,00"],
    ["009", "Relatorio mensal de acuracia", "12", "45,00", "540,00"],
    ["010", "Suporte tecnico em horario comercial", "12", "180,00", "2.160,00"],
]


def build_table_page() -> Image.Image:
    """Página com tabela: é onde o PP-StructureV3 precisa justificar o custo extra."""
    image = Image.new("RGB", A4_200DPI, (252, 252, 250))
    draw = ImageDraw.Draw(image)

    draw.text((150, 170), "DEMONSTRATIVO DE SERVICOS PRESTADOS", font=font(SANS_BOLD, 40), fill=(15, 15, 15))
    draw.text((150, 232), "Competencia 09/2026 - EXEMPLO SINTETICO LTDA - CNPJ 12.ABC.345/01DE-35", font=font(SANS, 26), fill=(70, 70, 70))

    left, top = 150, 320
    widths = [110, 640, 150, 230, 230]
    row_height = 74
    header_font = font(SANS_BOLD, 26)
    cell_font = font(SANS, 26)

    x = left
    for index, width in enumerate(widths):
        draw.rectangle([(x, top), (x + width, top + row_height)], fill=(228, 228, 224), outline=(60, 60, 60), width=2)
        draw.text((x + 14, top + row_height // 2), TABLE_HEADER[index], font=header_font, fill=(15, 15, 15), anchor="lm")
        x += width

    y = top + row_height
    for row in TABLE_ROWS:
        x = left
        for index, width in enumerate(widths):
            draw.rectangle([(x, y), (x + width, y + row_height)], outline=(60, 60, 60), width=1)
            anchor = "rm" if index >= 2 else "lm"
            text_x = x + width - 14 if index >= 2 else x + 14
            draw.text((text_x, y + row_height // 2), row[index], font=cell_font, fill=(20, 20, 20), anchor=anchor)
            x += width
        y += row_height

    total_left = left + widths[0] + widths[1] + widths[2]
    draw.rectangle([(total_left, y), (total_left + widths[3] + widths[4], y + row_height)], fill=(238, 238, 234), outline=(60, 60, 60), width=2)
    draw.text((total_left + 14, y + row_height // 2), "TOTAL GERAL", font=header_font, fill=(15, 15, 15), anchor="lm")
    draw.text((total_left + widths[3] + widths[4] - 14, y + row_height // 2), "4.896,00", font=header_font, fill=(15, 15, 15), anchor="rm")

    draw.text((150, y + row_height + 80), "Documento sintetico gerado para benchmark de OCR e extracao.", font=font(SANS, 24), fill=(110, 110, 110))

    watermark_diagonal(image)
    return image


def main() -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    card = build_cpf_card()
    text_page = build_text_page()
    table_page = build_table_page()

    artifacts = {
        # Cartão de CPF: limpo e digitalizado, para o tipo BR_CPF_CARD de ponta a ponta.
        "cpf-card-limpo.png": card,
        "cpf-card-escaneado.png": scan_effect(card, angle=-1.4, noise=26, blur=0.6),
        # Página densa: pior caso comum de latência por página.
        "pagina-texto-densa.png": text_page,
        "pagina-texto-densa-escaneada.png": scan_effect(text_page, angle=0.8, noise=20, blur=0.7),
        # Tabela: mede o que o PP-StructureV3 entrega a mais.
        "pagina-tabela.png": table_page,
        "pagina-tabela-escaneada.png": scan_effect(table_page, angle=-0.6, noise=18, blur=0.6),
    }

    for name, image in artifacts.items():
        path = OUTPUT_DIR / name
        image.save(path, optimize=True)
        print(f"{name:<36} {image.width:>5}x{image.height:<5} {path.stat().st_size:>9} bytes")

    print(f"\n{len(artifacts)} amostras em samples/synthetic/ocr/")


if __name__ == "__main__":
    main()
