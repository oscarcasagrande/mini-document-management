"""Gera as amostras sintéticas da Etapa 3: um documento de cada tipo estruturado.

Cada amostra sai com um `<nome>.expected.json`, a verdade de referência do que o extrator deve devolver
(valor normalizado e status por campo). Os testes unitários e o teste ponta a ponta leem esses mesmos
arquivos, então amostra e expectativa não têm como divergir.

Tudo aqui é fictício por construção: os CPFs e o CNPJ são os exemplos de manual (dígito verificador válido,
origem didática), os nomes não existem e cada peça carrega a marca AMOSTRA SINTETICA. Os layouts imitam a
estrutura de rótulo e valor dos documentos reais, mas não são cópia de nenhum: o desempenho em documento de
verdade é assunto da Etapa 4 (avaliação).

    docker run --rm -v "$PWD:/w" -w /w python:3.12-slim bash -c \
      "apt-get update -qq && apt-get install -y -qq fonts-dejavu-core >/dev/null \
       && pip install -q pillow && python scripts/make-stage3-samples.py"

Saída: samples/synthetic/documents/
"""

from __future__ import annotations

import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

OUTPUT_DIR = Path(__file__).resolve().parent.parent / "samples" / "synthetic" / "documents"

FONT_DIR = Path("/usr/share/fonts/truetype/dejavu")
SANS = FONT_DIR / "DejaVuSans.ttf"
SANS_BOLD = FONT_DIR / "DejaVuSans-Bold.ttf"
MONO = FONT_DIR / "DejaVuSansMono.ttf"

MARK_TEXT = "AMOSTRA SINTETICA - SEM VALOR LEGAL"

INK = (18, 18, 18)
GRAY = (95, 95, 95)
PAPER = (247, 245, 238)
LINE = (140, 140, 140)

# CPFs de manual, dígito verificador válido. O CNPJ alfanumérico é o exemplo do RF-012 e o numérico é o legado.
CPF_MARIA = "111.444.777-35"
CPF_CARLOS = "529.982.247-25"
CNPJ_ALPHANUMERIC = "12.ABC.345/01DE-35"
CNPJ_NUMERIC = "04.252.011/0001-10"


def font(path: Path, size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(str(path), size)


def footer_mark(image: Image.Image, top: int | None = None) -> None:
    """Carimbo em faixa, fora da área dos campos."""
    draw = ImageDraw.Draw(image)
    band_top = top if top is not None else image.height - 58
    draw.rectangle([(24, band_top), (image.width - 25, band_top + 32)], fill=(232, 226, 218))
    draw.text((image.width // 2, band_top + 16), MARK_TEXT, font=font(SANS_BOLD, 20), fill=(150, 60, 60), anchor="mm")


def labelled(draw: ImageDraw.ImageDraw, x: int, y: int, label: str, value: str, *, label_size: int = 20, value_size: int = 34, mono: bool = False) -> None:
    """Rótulo pequeno em cima e valor logo abaixo, como nos documentos brasileiros."""
    draw.text((x, y), label, font=font(SANS, label_size), fill=GRAY)
    draw.text((x, y + label_size + 12), value, font=font(MONO if mono else SANS_BOLD, value_size), fill=INK)


def wrap(text: str, typeface: ImageFont.FreeTypeFont, max_width: int) -> list[str]:
    words = text.split()
    lines: list[str] = []
    current = ""
    for word in words:
        candidate = f"{current} {word}".strip()
        if typeface.getlength(candidate) <= max_width:
            current = candidate
        else:
            lines.append(current)
            current = word
    if current:
        lines.append(current)
    return lines


def field(normalized: str | None, status: str = "VALID") -> dict:
    return {"normalized": normalized, "status": status}


# ------------------------------------------------------------------ MRZ (ICAO 9303, TD1)

def mrz_check(text: str) -> str:
    weights = (7, 3, 1)
    total = 0
    for index, char in enumerate(text):
        if char == "<":
            value = 0
        elif char.isdigit():
            value = int(char)
        else:
            value = ord(char) - ord("A") + 10
        total += value * weights[index % 3]
    return str(total % 10)


def build_mrz(document_number: str, birth: str, sex: str, expiry: str, names: str) -> list[str]:
    number = document_number.ljust(9, "<")
    line1 = "IDBRA" + number + mrz_check(number) + "<" * 15
    line2_head = birth + mrz_check(birth) + sex + expiry + mrz_check(expiry) + "BRA" + "<" * 11
    composite = line1[5:30] + line2_head[0:7] + line2_head[8:15] + line2_head[18:29]
    line2 = line2_head + mrz_check(composite)
    line3 = names.ljust(30, "<")[:30]
    assert len(line1) == len(line2) == len(line3) == 30, (len(line1), len(line2), len(line3))
    return [line1, line2, line3]


# ------------------------------------------------------------------ CIN

def build_cin() -> tuple[Image.Image, dict]:
    width, height = 1200, 1600
    image = Image.new("RGB", (width, height), (238, 236, 230))
    draw = ImageDraw.Draw(image)

    for top in (30, 830):
        draw.rectangle([(30, top), (width - 31, top + 740)], fill=PAPER, outline=LINE, width=3)

    # Frente
    draw.text((width // 2, 80), "REPÚBLICA FEDERATIVA DO BRASIL", font=font(SANS_BOLD, 32), fill=INK, anchor="mm")
    draw.text((width // 2, 124), "CARTEIRA DE IDENTIDADE NACIONAL", font=font(SANS_BOLD, 30), fill=INK, anchor="mm")
    draw.line([(80, 160), (width - 80, 160)], fill=LINE, width=2)

    labelled(draw, 80, 200, "NOME", "MARIA APARECIDA DA SILVA SOUZA")
    labelled(draw, 80, 330, "DATA DE NASCIMENTO", "14/03/1985")
    labelled(draw, 640, 330, "CPF", CPF_MARIA, mono=True)
    labelled(draw, 80, 460, "REGISTRO GERAL", "12.345.678-9")
    labelled(draw, 640, 460, "NATURALIDADE", "São Paulo - SP")
    labelled(draw, 80, 590, "DATA DE EXPEDIÇÃO", "20/05/2024")
    labelled(draw, 640, 590, "DATA DE VALIDADE", "20/05/2034")

    # Verso
    draw.text((width // 2, 880), "VERSO", font=font(SANS_BOLD, 26), fill=GRAY, anchor="mm")
    draw.text((80, 940), "FILIAÇÃO", font=font(SANS, 20), fill=GRAY)
    draw.text((80, 976), "JOSE DA SILVA SOUZA", font=font(SANS_BOLD, 34), fill=INK)
    draw.text((80, 1030), "ANA MARIA APARECIDA", font=font(SANS_BOLD, 34), fill=INK)

    mrz = build_mrz("123456789", "850314", "F", "340520", "SOUZA<<MARIA<APARECIDA<DA<SILVA")
    for index, line in enumerate(mrz):
        draw.text((80, 1290 + index * 56), line, font=font(MONO, 38), fill=INK)

    footer_mark(image, top=775)
    footer_mark(image, top=1535)

    expected = {
        "documentType": "BR_CIN",
        "fields": {
            "name": field("MARIA APARECIDA DA SILVA SOUZA"),
            "cpf": field("11144477735"),
            "rg": field("123456789"),
            "birthDate": field("1985-03-14"),
            "issueDate": field("2024-05-20"),
            "expirationDate": field("2034-05-20"),
            "birthPlace": field("SAO PAULO - SP"),
            "fatherName": field("JOSE DA SILVA SOUZA", "UNCERTAIN"),
            "motherName": field("ANA MARIA APARECIDA", "UNCERTAIN"),
            "mrz": field("\n".join(mrz)),
        },
    }
    return image, expected


# ------------------------------------------------------------------ CNH

def build_cnh() -> tuple[Image.Image, dict]:
    width, height = 1200, 800
    image = Image.new("RGB", (width, height), PAPER)
    draw = ImageDraw.Draw(image)
    draw.rectangle([(0, 0), (width - 1, height - 1)], outline=LINE, width=3)

    draw.text((width // 2, 46), "REPÚBLICA FEDERATIVA DO BRASIL", font=font(SANS_BOLD, 28), fill=INK, anchor="mm")
    draw.text((width // 2, 84), "MINISTÉRIO DOS TRANSPORTES", font=font(SANS, 22), fill=GRAY, anchor="mm")
    draw.text((width // 2, 114), "SECRETARIA NACIONAL DE TRÂNSITO", font=font(SANS, 22), fill=GRAY, anchor="mm")
    draw.text((width // 2, 158), "CARTEIRA NACIONAL DE HABILITAÇÃO", font=font(SANS_BOLD, 32), fill=INK, anchor="mm")
    draw.line([(60, 190), (width - 60, 190)], fill=LINE, width=2)

    draw.rectangle([(60, 230), (300, 540)], outline=LINE, width=3)
    draw.text((180, 385), "FOTO", font=font(SANS_BOLD, 30), fill=LINE, anchor="mm")

    labelled(draw, 340, 220, "NOME", "MARIA APARECIDA DA SILVA SOUZA")
    labelled(draw, 340, 320, "DOC. IDENTIDADE / ORG. EMISSOR / UF", "12.345.678-9 SSP SP", value_size=30)
    labelled(draw, 340, 420, "CPF", CPF_MARIA, mono=True)
    labelled(draw, 780, 420, "3 DATA NASCIMENTO", "14/03/1985")
    labelled(draw, 60, 580, "5 Nº REGISTRO", "04512345678", mono=True)
    labelled(draw, 440, 580, "9 CAT. HAB.", "AB")
    labelled(draw, 780, 580, "1ª HABILITAÇÃO", "01/07/2004")
    labelled(draw, 60, 680, "4a DATA EMISSÃO", "01/07/2024")
    labelled(draw, 440, 680, "4b VALIDADE", "01/07/2029")

    footer_mark(image, top=height - 46)

    expected = {
        "documentType": "BR_CNH",
        "fields": {
            "name": field("MARIA APARECIDA DA SILVA SOUZA"),
            "cpf": field("11144477735"),
            "birthDate": field("1985-03-14"),
            "registrationNumber": field("04512345678"),
            "category": field("AB"),
            "firstLicenseDate": field("2004-07-01"),
            "issueDate": field("2024-07-01"),
            "expirationDate": field("2029-07-01"),
        },
    }
    return image, expected


# ------------------------------------------------------------------ Comprovante de residência

def build_proof_of_address() -> tuple[Image.Image, dict]:
    width, height = 1240, 1754
    image = Image.new("RGB", (width, height), (252, 252, 250))
    draw = ImageDraw.Draw(image)

    draw.rectangle([(60, 60), (width - 61, 200)], fill=(224, 232, 240))
    draw.text((100, 96), "COMPANHIA ENERGÉTICA EXEMPLO S.A.", font=font(SANS_BOLD, 38), fill=INK)
    draw.text((100, 152), "FATURA DE ENERGIA ELÉTRICA", font=font(SANS_BOLD, 30), fill=(40, 70, 110))

    labelled(draw, 100, 250, "CLIENTE", "MARIA APARECIDA DA SILVA SOUZA")
    labelled(draw, 100, 370, "CPF/CNPJ", CPF_MARIA, mono=True)
    labelled(draw, 100, 490, "ENDEREÇO", "AVENIDA DOS TESTES, 250 APTO 71")
    labelled(draw, 100, 610, "BAIRRO", "VILA EXEMPLO")
    draw.text((100, 730), "CEP 04000-000  SÃO PAULO - SP", font=font(SANS_BOLD, 34), fill=INK)

    labelled(draw, 100, 880, "UNIDADE CONSUMIDORA", "1234567")
    labelled(draw, 660, 880, "REFERÊNCIA", "09/2026")
    labelled(draw, 100, 1010, "VENCIMENTO", "15/10/2026")
    labelled(draw, 660, 1010, "TOTAL A PAGAR", "R$ 187,45")
    labelled(draw, 100, 1140, "CONSUMO KWH", "312")

    draw.text((100, 1300), "Em caso de dúvida, ligue para a central de atendimento.", font=font(SANS, 24), fill=GRAY)
    footer_mark(image)

    expected = {
        "documentType": "BR_PROOF_OF_ADDRESS",
        "fields": {
            "holderName": field("MARIA APARECIDA DA SILVA SOUZA"),
            "holderDocument": field("11144477735"),
            "addressLine": field("AVENIDA DOS TESTES, 250 APTO 71"),
            "neighborhood": field("VILA EXEMPLO"),
            "city": field("SAO PAULO"),
            "state": field("SP"),
            "postalCode": field("04000000"),
            "referenceMonth": field("2026-09"),
            "dueDate": field("2026-10-15"),
            "serviceType": field("ELECTRICITY"),
        },
    }
    return image, expected


# ------------------------------------------------------------------ Cartão CNPJ

def cell(draw: ImageDraw.ImageDraw, box: tuple[int, int, int, int], label: str, value: str, value_size: int = 28) -> None:
    left, top, right, bottom = box
    draw.rectangle(box, outline=LINE, width=2)
    draw.text((left + 12, top + 8), label, font=font(SANS, 17), fill=GRAY)
    draw.text((left + 12, top + 42), value, font=font(SANS_BOLD, value_size), fill=INK)


def build_cnpj_card() -> tuple[Image.Image, dict]:
    width, height = 1240, 1754
    image = Image.new("RGB", (width, height), (252, 252, 250))
    draw = ImageDraw.Draw(image)

    draw.text((width // 2, 90), "REPÚBLICA FEDERATIVA DO BRASIL", font=font(SANS_BOLD, 32), fill=INK, anchor="mm")
    draw.text((width // 2, 136), "CADASTRO NACIONAL DA PESSOA JURÍDICA", font=font(SANS_BOLD, 30), fill=INK, anchor="mm")
    draw.text((width // 2, 180), "COMPROVANTE DE INSCRIÇÃO E DE SITUAÇÃO CADASTRAL", font=font(SANS, 24), fill=GRAY, anchor="mm")

    x0, x1 = 60, width - 60
    mid = width // 2
    y = 230
    cell(draw, (x0, y, mid, y + 110), "NÚMERO DE INSCRIÇÃO", CNPJ_ALPHANUMERIC + "  MATRIZ")
    cell(draw, (mid, y, x1, y + 110), "DATA DE ABERTURA", "01/03/2015")
    y += 110
    cell(draw, (x0, y, x1, y + 110), "NOME EMPRESARIAL", "EXEMPLO SINTETICO LTDA")
    y += 110
    cell(draw, (x0, y, x1, y + 110), "TÍTULO DO ESTABELECIMENTO (NOME DE FANTASIA)", "********")
    y += 110
    cell(draw, (x0, y, x1, y + 130), "CÓDIGO E DESCRIÇÃO DA ATIVIDADE ECONÔMICA PRINCIPAL", "62.01-5-01 - Desenvolvimento de programas de computador sob encomenda", 24)
    y += 130
    cell(draw, (x0, y, x1, y + 110), "CÓDIGO E DESCRIÇÃO DA NATUREZA JURÍDICA", "206-2 - Sociedade Empresária Limitada")
    y += 110
    cell(draw, (x0, y, 700, y + 110), "LOGRADOURO", "RUA DAS AMOSTRAS")
    cell(draw, (700, y, 900, y + 110), "NÚMERO", "1000")
    cell(draw, (900, y, x1, y + 110), "COMPLEMENTO", "SALA 12")
    y += 110
    cell(draw, (x0, y, 330, y + 110), "CEP", "01.000-000")
    cell(draw, (330, y, 700, y + 110), "BAIRRO/DISTRITO", "CENTRO")
    cell(draw, (700, y, 1060, y + 110), "MUNICÍPIO", "SAO PAULO")
    cell(draw, (1060, y, x1, y + 110), "UF", "SP")
    y += 110
    cell(draw, (x0, y, mid, y + 110), "SITUAÇÃO CADASTRAL", "ATIVA")
    cell(draw, (mid, y, x1, y + 110), "DATA DA SITUAÇÃO CADASTRAL", "01/03/2015")

    footer_mark(image)

    expected = {
        "documentType": "BR_CNPJ_CARD",
        "fields": {
            "cnpj": field("12ABC34501DE35"),
            "openingDate": field("2015-03-01"),
            "legalName": field("EXEMPLO SINTETICO LTDA"),
            "tradeName": field(None, "NOT_FOUND"),
            "mainActivityCode": field("6201501"),
            "mainActivityDescription": field("DESENVOLVIMENTO DE PROGRAMAS DE COMPUTADOR SOB ENCOMENDA"),
            "legalNature": field("206-2 - SOCIEDADE EMPRESARIA LIMITADA"),
            "street": field("RUA DAS AMOSTRAS"),
            "number": field("1000"),
            "complement": field("SALA 12"),
            "postalCode": field("01000000"),
            "neighborhood": field("CENTRO"),
            "city": field("SAO PAULO"),
            "state": field("SP"),
            "registrationStatus": field("ATIVA"),
            "registrationStatusDate": field("2015-03-01"),
        },
    }
    return image, expected


# ------------------------------------------------------------------ CCMEI

def build_ccmei() -> tuple[Image.Image, dict]:
    width, height = 1240, 1754
    image = Image.new("RGB", (width, height), (252, 252, 250))
    draw = ImageDraw.Draw(image)

    draw.text((width // 2, 90), "CCMEI", font=font(SANS_BOLD, 40), fill=(20, 90, 60), anchor="mm")
    draw.text((width // 2, 140), "CERTIFICADO DA CONDIÇÃO DE MICROEMPREENDEDOR INDIVIDUAL", font=font(SANS_BOLD, 26), fill=INK, anchor="mm")
    draw.text((width // 2, 180), "Portal do Empreendedor", font=font(SANS, 22), fill=GRAY, anchor="mm")
    draw.line([(80, 210), (width - 80, 210)], fill=LINE, width=2)

    labelled(draw, 80, 240, "NOME EMPRESARIAL", f"MARIA APARECIDA DA SILVA SOUZA {CPF_MARIA.replace('.', '').replace('-', '')}", value_size=32)
    labelled(draw, 80, 350, "NOME FANTASIA", "MARIA DOCES")
    labelled(draw, 80, 460, "CNPJ", CNPJ_NUMERIC, mono=True)
    labelled(draw, 660, 460, "DATA DE ABERTURA", "10/02/2020")
    labelled(draw, 80, 570, "CAPITAL SOCIAL", "R$ 5.000,00")
    labelled(draw, 80, 680, "OCUPAÇÃO PRINCIPAL", "10.91-1-02 - Fabricação de produtos de padaria e confeitaria", value_size=28)
    draw.text((80, 800), "ENDEREÇO COMERCIAL", font=font(SANS, 20), fill=GRAY)
    draw.text((80, 836), "RUA DAS AMOSTRAS, 100 - CENTRO", font=font(SANS_BOLD, 32), fill=INK)
    draw.text((80, 884), "SAO PAULO/SP", font=font(SANS_BOLD, 32), fill=INK)
    draw.text((80, 932), "CEP 01000-000", font=font(SANS_BOLD, 32), fill=INK)

    draw.text((80, 1040), "DADOS DO EMPRESÁRIO", font=font(SANS_BOLD, 26), fill=(20, 90, 60))
    labelled(draw, 80, 1090, "NOME", "MARIA APARECIDA DA SILVA SOUZA")
    labelled(draw, 80, 1200, "CPF", CPF_MARIA, mono=True)
    labelled(draw, 660, 1200, "DATA DE NASCIMENTO", "14/03/1985")
    labelled(draw, 80, 1330, "DATA DE EMISSÃO", "01/09/2026")

    footer_mark(image)

    expected = {
        "documentType": "BR_CCMEI",
        "fields": {
            "cnpj": field("04252011000110"),
            "legalName": field("MARIA APARECIDA DA SILVA SOUZA 11144477735"),
            "tradeName": field("MARIA DOCES"),
            "openingDate": field("2020-02-10"),
            "shareCapital": field("5000.00"),
            "mainActivityCode": field("1091102"),
            "mainActivityDescription": field("FABRICACAO DE PRODUTOS DE PADARIA E CONFEITARIA"),
            "address": field("RUA DAS AMOSTRAS, 100 - CENTRO SAO PAULO/SP CEP 01000-000"),
            "postalCode": field("01000000"),
            "city": field("SAO PAULO"),
            "state": field("SP"),
            "holderName": field("MARIA APARECIDA DA SILVA SOUZA"),
            "holderCpf": field("11144477735"),
            "holderBirthDate": field("1985-03-14"),
            "certificateDate": field("2026-09-01"),
        },
    }
    return image, expected


# ------------------------------------------------------------------ Contrato social (3 páginas)

HEADER = f"EXEMPLO SINTETICO LTDA - CNPJ {CNPJ_ALPHANUMERIC} - NIRE 35234567890"

CONTRACT_PARAGRAPHS: list[tuple[str, str]] = [
    ("title", "INSTRUMENTO PARTICULAR DE CONTRATO SOCIAL"),
    ("body",
     "Pelo presente instrumento particular, entre si, MARIA APARECIDA DA SILVA SOUZA, brasileira, casada, "
     "empresária, nascida em 14/03/1985, portadora da cédula de identidade RG nº 12.345.678-9, inscrita no CPF "
     f"sob o nº {CPF_MARIA}, residente e domiciliada na Avenida dos Testes, 250, apto 71, Vila Exemplo, "
     "São Paulo/SP, CEP 04000-000, e CARLOS EDUARDO PEREIRA LIMA, brasileiro, solteiro, empresário, nascido em "
     "02/11/1979, portador da cédula de identidade RG nº 23.456.789-0, inscrito no CPF sob o nº "
     f"{CPF_CARLOS}, residente e domiciliado na Rua das Palmeiras, 80, Jardim Teste, São Paulo/SP, CEP "
     "05000-000, resolvem constituir uma sociedade empresária limitada, mediante as cláusulas seguintes."),
    ("heading", "CLÁUSULA PRIMEIRA - DA DENOMINAÇÃO E DA SEDE"),
    ("body",
     "A sociedade girará sob a denominação social de EXEMPLO SINTETICO LTDA, com sede na Rua das Amostras, 1000, "
     "sala 12, bairro Centro, São Paulo/SP, CEP 01000-000, podendo abrir filiais em qualquer parte do território "
     "nacional por deliberação dos sócios."),
    ("heading", "CLÁUSULA SEGUNDA - DO OBJETO SOCIAL"),
    ("body",
     "O objeto social será o desenvolvimento de programas de computador sob encomenda (CNAE 62.01-5-01) e o "
     "licenciamento de programas de computador. A sociedade poderá participar de outras sociedades, "
     "como sócia ou acionista, desde que aprovado pelos sócios."),
    ("heading", "CLÁUSULA TERCEIRA - DO CAPITAL SOCIAL"),
    ("body",
     "O capital social é de R$ 100.000,00 (cem mil reais), dividido em 100.000 (cem mil) quotas no valor nominal "
     "de R$ 1,00 (um real) cada uma, totalmente subscritas e integralizadas em moeda corrente nacional, "
     "assim distribuídas entre os sócios: MARIA APARECIDA DA SILVA SOUZA, 60.000 (sessenta mil) quotas, "
     "e CARLOS EDUARDO PEREIRA LIMA, 40.000 (quarenta mil) quotas."),
    ("heading", "CLÁUSULA QUARTA - DA RESPONSABILIDADE DOS SÓCIOS"),
    ("body",
     "A responsabilidade de cada sócio é restrita ao valor de suas quotas, mas todos respondem solidariamente "
     "pela integralização do capital social, nos termos do artigo 1.052 do Código Civil."),
    ("pagebreak", ""),
    ("heading", "CLÁUSULA QUINTA - DA ADMINISTRAÇÃO"),
    ("body",
     "A administração da sociedade caberá à sócia MARIA APARECIDA DA SILVA SOUZA, com poderes para representar a "
     "sociedade ativa e passivamente, vedado o uso da denominação social em negócios estranhos ao objeto social, "
     "especialmente em fianças, avais e endossos de favor."),
    ("heading", "CLÁUSULA SEXTA - DO PRAZO"),
    ("body",
     "A sociedade iniciará suas atividades na data do arquivamento deste instrumento e seu prazo de duração é "
     "indeterminado."),
    ("heading", "CLÁUSULA SÉTIMA - DO EXERCÍCIO SOCIAL"),
    ("body",
     "O exercício social terminará em 31 de dezembro de cada ano, quando será levantado o balanço patrimonial "
     "e o resultado econômico, cabendo aos sócios, na proporção de suas quotas, os lucros apurados e as perdas "
     "eventualmente verificadas."),
    ("heading", "CLÁUSULA OITAVA - DA RETIRADA PRO LABORE"),
    ("body",
     "Os sócios poderão fixar, de comum acordo, uma retirada mensal a título de pro labore, observadas as "
     "disposições da legislação tributária e previdenciária vigentes."),
    ("pagebreak", ""),
    ("heading", "CLÁUSULA NONA - DA CESSÃO DE QUOTAS"),
    ("body",
     "As quotas são indivisíveis e não poderão ser cedidas ou transferidas a terceiros sem o consentimento do "
     "outro sócio, a quem fica assegurado o direito de preferência em igualdade de condições."),
    ("heading", "CLÁUSULA DÉCIMA - DO FORO"),
    ("body",
     "Fica eleito o foro da comarca de São Paulo/SP para dirimir quaisquer dúvidas oriundas do presente "
     "instrumento, com renúncia expressa a qualquer outro, por mais privilegiado que seja."),
    ("body",
     "E, por estarem assim justos e contratados, assinam o presente instrumento em duas vias de igual teor."),
    ("body", "São Paulo, 24 de setembro de 2026."),
    ("sign", "MARIA APARECIDA DA SILVA SOUZA"),
    ("sign", "CARLOS EDUARDO PEREIRA LIMA"),
]


def build_contract_pages() -> list[Image.Image]:
    """Contrato em páginas A4 a 200 DPI, sempre em três páginas: duas quebras explícitas espalham os dados."""
    page_size = (1654, 2339)
    margin = 150
    body_font = font(SANS, 30)
    heading_font = font(SANS_BOLD, 30)
    title_font = font(SANS_BOLD, 38)
    text_width = page_size[0] - 2 * margin
    bottom_limit = page_size[1] - 260

    pages: list[Image.Image] = []
    image = draw = None
    y = 0

    def new_page() -> None:
        nonlocal image, draw, y
        image = Image.new("RGB", page_size, (252, 252, 250))
        draw = ImageDraw.Draw(image)
        draw.text((margin, 90), HEADER, font=font(SANS, 24), fill=GRAY)
        draw.line([(margin, 130), (page_size[0] - margin, 130)], fill=LINE, width=2)
        pages.append(image)
        y = 190

    new_page()
    for kind, text in CONTRACT_PARAGRAPHS:
        if kind == "pagebreak":
            new_page()
            continue

        typeface = {"title": title_font, "heading": heading_font}.get(kind, body_font)
        lines = wrap(text, typeface, text_width - (80 if kind == "sign" else 0))
        needed = len(lines) * 46 + (40 if kind != "sign" else 90)

        if y + needed > bottom_limit:
            new_page()

        if kind == "sign":
            y += 50
            draw.line([(margin, y), (margin + 700, y)], fill=INK, width=2)
            y += 10

        for line in lines:
            draw.text((margin, y), line, font=typeface, fill=INK)
            y += 46
        y += 30

    # O conteúdo deve ocupar exatamente três páginas: uma quarta seria um contrato diferente do esperado.
    assert len(pages) == 3, f"o contrato ocupou {len(pages)} páginas, esperado 3"

    for number, page in enumerate(pages, start=1):
        page_draw = ImageDraw.Draw(page)
        page_draw.text((page_size[0] - margin, page_size[1] - 150), f"página {number} de {len(pages)}", font=font(SANS, 24), fill=GRAY, anchor="rs")
        footer_mark(page, top=page_size[1] - 90)

    return pages


CONTRACT_EXPECTED = {
    "documentType": "BR_SOCIAL_CONTRACT",
    "fields": {
        "companyName": field("EXEMPLO SINTETICO LTDA"),
        "cnpj": field("12ABC34501DE35"),
        "nire": field("35234567890"),
        "shareCapital": field("100000.00"),
        "headquarters": field("RUA DAS AMOSTRAS, 1000, SALA 12, BAIRRO CENTRO, SAO PAULO/SP, CEP 01000-000"),
        "headquartersPostalCode": field("01000000"),
        "corporatePurpose": field("O DESENVOLVIMENTO DE PROGRAMAS DE COMPUTADOR SOB ENCOMENDA (CNAE 62.01-5-01) E O LICENCIAMENTO DE PROGRAMAS DE COMPUTADOR"),
        "contractDate": field("2026-09-24"),
        "partners[0].name": field("MARIA APARECIDA DA SILVA SOUZA"),
        "partners[0].cpf": field("11144477735"),
        "partners[1].name": field("CARLOS EDUARDO PEREIRA LIMA"),
        "partners[1].cpf": field("52998224725"),
    },
}


def main() -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    single_page = {
        "cin-frente-verso": build_cin(),
        "cnh": build_cnh(),
        "comprovante-residencia": build_proof_of_address(),
        "cartao-cnpj": build_cnpj_card(),
        "ccmei": build_ccmei(),
    }

    for name, (image, expected) in single_page.items():
        path = OUTPUT_DIR / f"{name}.png"
        image.save(path, optimize=True)
        (OUTPUT_DIR / f"{name}.expected.json").write_text(json.dumps(expected, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"{name + '.png':<34} {image.width:>5}x{image.height:<5} {path.stat().st_size:>9} bytes")

    pages = build_contract_pages()
    pdf_path = OUTPUT_DIR / "contrato-social.pdf"
    pages[0].save(pdf_path, save_all=True, append_images=pages[1:], resolution=200.0)
    (OUTPUT_DIR / "contrato-social.expected.json").write_text(json.dumps(CONTRACT_EXPECTED, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{'contrato-social.pdf':<34} {len(pages)} páginas {pdf_path.stat().st_size:>9} bytes")


if __name__ == "__main__":
    main()
