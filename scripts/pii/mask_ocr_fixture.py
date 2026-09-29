"""Mascara uma captura de OCR real, devolvendo uma fixture pronta para `tests/unit/.../Fixtures/ocr/`.

Motivo de existir: uma fixture mascarada à mão (`elektro-real.ocr.json`, nesta mesma sessão) deixou
cidade, bairro e CEP reais do titular sem querer — o rótulo "endereço" cobria a rua, mas não a cidade.
Mascarar à mão não é confiável; **este script é o jeito aceito de gerar fixture de documento real daqui
em diante** (ver CLAUDE.md).

Uso:
    python scripts/pii/mask_ocr_fixture.py --input captura-real.json --output fixture-mascarada.json \\
        [--names-file nomes-reais.txt] [--source "nome-da-amostra (mascarado)"]

Entrada: o mesmo formato de `scripts/capture-ocr-fixtures.py` e do que `GET
.../extraction-diagnostics` devolve em `pages[].blocks[]` (`text`, `confidence`, `boundingBox`, mais
campos extras que são preservados sem alteração): `{"source": ..., "modelVersion": ..., "pages":
[{"page": N, "blocks": [{"text": ..., "confidence": ..., "boundingBox": [...]}]}]}`.

O que é substituído, e como:
- CPF, CNPJ e CEP: por regex + dígito verificador (mesmo algoritmo de `scan.py`), por um valor da lista
  de sintéticos já aprovados do projeto (`synthetic_values.py`) — nunca um valor novo inventado, para a
  autoconferência no fim (que roda o próprio `scan.py` sobre a saída) sempre fechar limpa por construção.
  Mesmo valor real, mesmo sintético, em todo o documento; valores reais diferentes recebem sintéticos
  diferentes (até a lista acabar, aí repete com aviso).
- Datas (`DD/MM/AAAA` e `Mês/AAAA`): o ano vira um sintético calculado a partir do ano real (nunca o dado
  real, mas continua uma data válida); dia e mês não são pessoais por si só e ficam.
- Endereço (linha começando com R/RUA/AV/AVENIDA/PRAÇA/ALAMEDA/... ou linha "CIDADE - UF - CEP ..."):
  heurística automática, ligada por padrão, cada substituição avisada em stderr para revisão.
  `--no-heuristics` desliga e deixa só o que o arquivo de nomes cobre.
- Nome de pessoa ou empresa: **sem heurística automática**. Testado contra um documento real: uma
  heurística de "2 a 6 palavras maiúsculas" pega tanto o nome do titular quanto rótulo de fatura em
  maiúsculas ("ITENS DE FATURA", "RESERVADO AO FISCO", "TOTAL A PAGAR") e corrompe a estrutura da
  fixture — não existe sinal estrutural confiável para distinguir os dois sem um modelo de NER, fora do
  escopo de um script de só biblioteca padrão. `--names-file` é obrigatório na prática para mascarar
  documento com pessoa/empresa: aceita um arquivo (mesma convenção de `DOCREADER_PII_NAMES_FILE` do
  `scan.py`, mas aqui usado para *substituir*, não só detectar) com uma entrada por linha, `TEXTO REAL`
  ou `TEXTO REAL => SUBSTITUTO`; sem `=>`, um substituto genérico é escolhido automaticamente.

Ao final, a própria varredura de vazamento (`scan.scan_paths`) roda sobre o arquivo de saída. Achado
residual é um erro (saída ainda é escrita, para inspeção, mas o script sai com código 1) — nunca uma
fixture "mascarada" que a varredura reprovaria.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import unicodedata
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import scan  # noqa: E402
from synthetic_values import (  # noqa: E402
    SYNTHETIC_CEPS,
    SYNTHETIC_CNH_REGISTRATIONS,
    SYNTHETIC_CNPJS,
    SYNTHETIC_CPFS,
    SYNTHETIC_RGS,
)

_MONTHS_PT = [
    "janeiro", "fevereiro", "março", "abril", "maio", "junho",
    "julho", "agosto", "setembro", "outubro", "novembro", "dezembro",
]

_DATE_DMY = re.compile(r"\b(\d{2})/(\d{2})/(\d{4})\b")
_DATE_MONTH_YEAR = re.compile(
    r"\b(" + "|".join(_MONTHS_PT) + r")/(\d{4})\b", re.IGNORECASE
)

# Só o começo de linha, com espaço depois do prefixo — "R$475,44" (moeda) não pode bater com "R" de "Rua".
_ADDRESS_START = re.compile(
    r"^\s*(R|RUA|AV|AVENIDA|PRA[CÇ]A|ALAMEDA|TRAVESSA|ROD|RODOVIA|ESTRADA)\.?\s+[A-ZÀ-Üa-zà-ü]", re.IGNORECASE
)
_CITY_STATE_CEP = re.compile(
    r"^\s*([A-ZÀ-Ü][A-ZÀ-Ü '.]+?)\s*-\s*([A-Z]{2})\s*-?\s*CEP[:\s]*(\d{5}-\d{3})\s*$", re.IGNORECASE
)
_COMPANY_SUFFIX = re.compile(r"\b(LTDA|S\.?A\.?|ME|EIRELI|EPP|MEI)\b\.?\s*$", re.IGNORECASE)


def _fold(text: str) -> str:
    decomposed = unicodedata.normalize("NFKD", text)
    return "".join(c for c in decomposed if not unicodedata.combining(c)).upper()


class _Pool:
    """Cicla por uma lista já aprovada de valores sintéticos, mapeando cada valor real distinto para um
    sintético fixo (mesmo real -> mesmo sintético dentro de uma execução)."""

    def __init__(self, values: frozenset[str], label: str) -> None:
        self._values = sorted(values)
        self._label = label
        self._assigned: dict[str, str] = {}

    def get(self, real_digits: str) -> str:
        if real_digits in self._assigned:
            return self._assigned[real_digits]
        if not self._values:
            raise SystemExit(f"synthetic_values.py não tem nenhum {self._label} sintético cadastrado")
        index = len(self._assigned) % len(self._values)
        if len(self._assigned) >= len(self._values):
            print(
                f"aviso: mais {self._label} distintos do que sintéticos cadastrados — reciclando "
                f"'{self._values[index]}'",
                file=sys.stderr,
            )
        chosen = self._values[index]
        self._assigned[real_digits] = chosen
        return chosen


def _format_like(original_raw: str, normalized_synthetic: str) -> str:
    """Reaplica a pontuação de `original_raw` (posições de '.', '-', '/') sobre os dígitos sintéticos."""
    result = []
    digit_iter = iter(normalized_synthetic)
    for char in original_raw:
        if char.isalnum():
            result.append(next(digit_iter, char))
        else:
            result.append(char)
    return "".join(result)


class Masker:
    def __init__(self, names_file: Path | None) -> None:
        self._cpf_pool = _Pool(SYNTHETIC_CPFS, "CPF")
        self._cnh_pool = _Pool(SYNTHETIC_CNH_REGISTRATIONS, "registro de CNH")
        self._cnpj_pool = _Pool(SYNTHETIC_CNPJS, "CNPJ")
        self._cep_pool = _Pool(SYNTHETIC_CEPS, "CEP")
        self._rg_pool = _Pool(SYNTHETIC_RGS, "RG")
        self._text_counter = 0
        self._company_counter = 0
        self._address_counter = 0
        self._explicit: list[tuple[str, str, str]] = []  # (folded real, real, synthetic)
        if names_file is not None and names_file.is_file():
            self._load_explicit(names_file)

    def _load_explicit(self, names_file: Path) -> None:
        for raw_line in names_file.read_text(encoding="utf-8").splitlines():
            stripped = raw_line.strip()
            if not stripped or stripped.startswith("#"):
                continue
            if "=>" in stripped:
                real, synthetic = (part.strip() for part in stripped.split("=>", 1))
            else:
                real = stripped
                synthetic = self._generic_replacement_for(real)
            self._explicit.append((_fold(real), real, synthetic))
        # Trechos mais longos primeiro, para "JOAO BATISTA SILVA" não deixar sobra de "SILVA" se também
        # existir uma entrada separada para o sobrenome.
        self._explicit.sort(key=lambda item: len(item[0]), reverse=True)

    def _generic_replacement_for(self, real: str) -> str:
        """Sem `=>` explícito, o substituto é genérico: não tenta adivinhar se o texto é nome de pessoa,
        de bairro ou de cidade (`--names-file` cobre os três com a mesma mecânica), só marca "é um
        exemplo" de forma clara. Único caso com um rótulo mais específico é razão social, pelo sufixo
        (LTDA/S.A./ME/EIRELI/EPP/MEI), porque extratores de contrato social esperam esse formato.
        """
        if _COMPANY_SUFFIX.search(real):
            self._company_counter += 1
            return f"EMPRESA EXEMPLO {self._company_counter} LTDA"
        self._text_counter += 1
        return f"TEXTO EXEMPLO {self._text_counter}"

    def _apply_explicit(self, text: str) -> str:
        if not self._explicit:
            return text
        folded_text = _fold(text)
        for folded_real, real, synthetic in self._explicit:
            if folded_real in folded_text:
                # Substituição por posição dobrada: como o dobramento preserva o comprimento por
                # caractere (remove só marcas de combinação), a mesma fatia de índices vale no texto
                # original.
                start = folded_text.find(folded_real)
                while start != -1:
                    end = start + len(folded_real)
                    text = text[:start] + synthetic + text[end:]
                    folded_text = _fold(text)
                    start = folded_text.find(folded_real)
        return text

    def _mask_cpf_or_cnh(self, match: re.Match[str]) -> str:
        digits = re.sub(r"\D", "", match.group(0))
        if digits in scan._SYNTHETIC_ELEVEN_DIGIT:
            return match.group(0)
        if scan.is_valid_cpf(digits):
            return _format_like(match.group(0), self._cpf_pool.get(digits))
        if scan.is_valid_cnh_registration(digits):
            return _format_like(match.group(0), self._cnh_pool.get(digits))
        return match.group(0)

    def _mask_cnpj(self, match: re.Match[str]) -> str:
        digits = re.sub(r"\D", "", match.group(0))
        if digits in SYNTHETIC_CNPJS:
            return match.group(0)
        if not scan.is_valid_cnpj(digits):
            return match.group(0)
        return _format_like(match.group(0), self._cnpj_pool.get(digits))

    def _mask_cpf_cnpj(self, text: str) -> str:
        # Mesma ordem de scan.py: CNPJ (14) antes de CPF/CNH (11), formatado antes de cru, para um CNPJ
        # com pontuação não sobrar pedaço batendo com o regex de CPF cru.
        text = scan._CNPJ_FORMATTED.sub(self._mask_cnpj, text)
        text = scan._CNPJ_BARE.sub(self._mask_cnpj, text)
        text = scan._CPF_FORMATTED.sub(self._mask_cpf_or_cnh, text)
        text = scan._CPF_BARE.sub(self._mask_cpf_or_cnh, text)
        return text

    def _mask_cep(self, text: str) -> str:
        def replace(match: re.Match[str]) -> str:
            digits = re.sub(r"\D", "", match.group(0))
            if digits in SYNTHETIC_CEPS:
                return match.group(0)
            synthetic_digits = self._cep_pool.get(digits)
            return _format_like(match.group(0), synthetic_digits)

        return scan._CEP_FORMATTED.sub(replace, text)

    def _mask_rg(self, text: str) -> str:
        def replace(match: re.Match[str]) -> str:
            raw = match.group(0)
            normalized = re.sub(r"[.\-]", "", raw).upper()
            if normalized in SYNTHETIC_RGS:
                return raw
            synthetic = self._rg_pool.get(normalized)
            return _format_like(raw, synthetic)

        return scan._RG_FORMATTED.sub(replace, text)

    def _mask_dates(self, text: str) -> str:
        def replace_dmy(match: re.Match[str]) -> str:
            day, month, year = match.groups()
            synthetic_year = 2030 + (int(year) % 20)
            return f"{day}/{month}/{synthetic_year}"

        def replace_month_year(match: re.Match[str]) -> str:
            month_name, year = match.groups()
            synthetic_year = 2030 + (int(year) % 20)
            return f"{month_name}/{synthetic_year}"

        text = _DATE_DMY.sub(replace_dmy, text)
        text = _DATE_MONTH_YEAR.sub(replace_month_year, text)
        return text

    def _mask_heuristic(self, text: str) -> str:
        """Só endereço, que tem um sinal estrutural confiável (prefixo de logradouro, ou linha "cidade -
        UF - CEP"). Nome de pessoa/empresa NÃO entra aqui: testado contra um documento real, uma
        heurística de "2 a 6 palavras maiúsculas" pega tanto nome quanto rótulo de fatura em maiúsculas
        ("ITENS DE FATURA", "RESERVADO AO FISCO", "TOTAL A PAGAR") e corrompe a estrutura da fixture. Para
        nome, use `--names-file` — é a única substituição confiável que este script faz.
        """
        stripped = text.strip()

        city_state_cep = _CITY_STATE_CEP.match(stripped)
        if city_state_cep:
            self._address_counter += 1
            print(f"aviso: heurística substituiu linha de cidade/UF/CEP: '{stripped[:40]}...'", file=sys.stderr)
            city, uf, cep_raw = city_state_cep.groups()
            cep_digits = re.sub(r"\D", "", cep_raw)
            synthetic_cep_digits = self._cep_pool.get(cep_digits) if cep_digits not in SYNTHETIC_CEPS else cep_digits
            synthetic_cep = _format_like(cep_raw, synthetic_cep_digits)
            return text.replace(stripped, f"CIDADE EXEMPLO {self._address_counter} - {uf} - CEP {synthetic_cep}")

        if _ADDRESS_START.match(stripped):
            self._address_counter += 1
            print(f"aviso: heurística substituiu linha de endereço: '{stripped[:40]}...'", file=sys.stderr)
            return text.replace(stripped, f"R EXEMPLO DA SILVA, {self._address_counter}")

        return text

    def mask(self, text: str, use_heuristics: bool) -> str:
        text = self._apply_explicit(text)
        text = self._mask_cpf_cnpj(text)
        text = self._mask_cep(text)
        text = self._mask_rg(text)
        text = self._mask_dates(text)
        if use_heuristics:
            text = self._mask_heuristic(text)
        return text


def mask_capture(capture: dict, names_file: Path | None, use_heuristics: bool) -> dict:
    masker = Masker(names_file)
    pages = []
    for page in capture.get("pages", []):
        blocks = []
        for block in page.get("blocks", []):
            masked_block = dict(block)
            masked_block["text"] = masker.mask(block["text"], use_heuristics)
            blocks.append(masked_block)
        masked_page = dict(page)
        masked_page["blocks"] = blocks
        pages.append(masked_page)

    masked_capture = dict(capture)
    masked_capture["pages"] = pages
    return masked_capture


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--input", required=True, type=Path, help="captura de OCR real (JSON)")
    parser.add_argument("--output", required=True, type=Path, help="fixture mascarada de saída (JSON)")
    parser.add_argument("--names-file", type=Path, default=None, help="nomes/trechos reais a substituir")
    parser.add_argument("--source", default=None, help="valor do campo 'source' na saída")
    parser.add_argument("--no-heuristics", action="store_true", help="desliga a heurística de endereço")
    args = parser.parse_args(argv)

    capture = json.loads(args.input.read_text(encoding="utf-8"))
    masked = mask_capture(capture, args.names_file, use_heuristics=not args.no_heuristics)
    if args.source:
        masked["source"] = args.source

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(masked, ensure_ascii=False, indent=1) + "\n",
        encoding="utf-8",
    )
    print(f"-> {args.output}")

    residual = scan.scan_paths([args.output], names_file=args.names_file)
    if residual:
        print(f"\nautoconferência falhou: {len(residual)} achado(s) ainda no arquivo mascarado:", file=sys.stderr)
        for finding in residual:
            print(f"  {finding}", file=sys.stderr)
        print(
            "\nO arquivo foi escrito mesmo assim, para inspeção — não use como fixture antes de corrigir.",
            file=sys.stderr,
        )
        return 1

    print("autoconferência: nenhum vazamento na fixture mascarada")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
