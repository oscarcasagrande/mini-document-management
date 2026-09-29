"""Varredura de vazamento de dado pessoal real em arquivos versionados.

Duas vezes nesta PoC um dado pessoal real de um documento real escapou para o repositório (um CPF de
cartão copiado para teste na Etapa 3, e cidade/bairro/CEP reais numa fixture mascarada à mão) — os dois
pegos só na auditoria manual antes do push. Esta varredura existe para não depender de auditoria manual
de novo: roda como teste (`tests/pii_scan/test_pii_scan.py`) e também como CLI.

O que é achado:
- CPF e CNPJ (formato numérico) com dígito verificador válido (mesmo algoritmo de
  `src/DocReader.Domain/Validation/Cpf.cs`/`Cnpj.cs`) que não estejam na lista de valores sintéticos
  conhecidos do projeto (`synthetic_values.py`).
- Número de registro de CNH (algoritmo do DENATRAN, `CnhRegistration.cs`) e título de eleitor (algoritmo
  do TSE) com dígito verificador válido, mesma regra de lista.
- CEP em formato válido (`\\d{5}-\\d{3}`) e RG em formato comum (`\\d{1,2}.\\d{3}.\\d{3}-[\\dX]`) — sem
  dígito verificador nacional, então a checagem é só de formato + lista, não de checksum.
- Nomes de pessoa/empresa de documentos reais desta sessão, lidos de um arquivo **fora do repositório**
  (nunca no código deste scanner), cujo caminho vem da variável de ambiente `DOCREADER_PII_NAMES_FILE`.
  Sem a variável, essa parte é pulada com aviso — não falha a varredura, porque o arquivo não pode existir
  fora do ambiente de quem fez a auditoria original.

Só biblioteca padrão, sem dependência de rede: precisa rodar rápido e sempre, não só na auditoria.
"""

from __future__ import annotations

import re
import subprocess
import sys
import unicodedata
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

sys.path.insert(0, str(Path(__file__).resolve().parent))
from synthetic_values import (  # noqa: E402
    SYNTHETIC_CEPS,
    SYNTHETIC_CNH_REGISTRATIONS,
    SYNTHETIC_CNPJS,
    SYNTHETIC_CPFS,
    SYNTHETIC_RGS,
    SYNTHETIC_TITULOS,
)

REPO_ROOT = Path(__file__).resolve().parent.parent.parent

# CPF e número de registro de CNH têm 11 dígitos e algoritmos diferentes, mas nada impede que um valor
# sintético construído para um propósito também feche o checksum do outro por coincidência (aconteceu com
# três vetores de CnhRegistrationTests.cs). Um valor conhecido para qualquer um dos dois não é achado em
# nenhum dos dois.
_SYNTHETIC_ELEVEN_DIGIT = SYNTHETIC_CPFS | SYNTHETIC_CNH_REGISTRATIONS

# Extensões binárias óbvias: nunca decodificam como texto, então nem tentamos.
_BINARY_EXTENSIONS = frozenset(
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".pdf", ".woff", ".woff2", ".ttf", ".eot",
        ".zip", ".gz", ".tar", ".7z", ".dll", ".exe", ".so", ".dylib", ".pyc", ".class", ".otf",
    }
)

_CPF_FORMATTED = re.compile(r"\d{3}\.\d{3}\.\d{3}-\d{2}")
# Além de não fazer parte de um dígito maior, um valor "puro" não pode ser vizinho de um ponto: senão a
# mantissa de um número de ponto flutuante (durations_ms de um benchmark, por exemplo) vira falso
# positivo sempre que o resto do checksum fechar por coincidência.
_CPF_BARE = re.compile(r"(?<![\d.])\d{11}(?![\d.])")
_CNPJ_FORMATTED = re.compile(r"\d{2}\.\d{3}\.\d{3}/\d{4}-\d{2}")
_CNPJ_BARE = re.compile(r"(?<![\d.])\d{14}(?![\d.])")
_CEP_FORMATTED = re.compile(r"(?<!\d)\d{5}-\d{3}(?!\d)")
_RG_FORMATTED = re.compile(r"(?<!\d)\d{1,2}\.\d{3}\.\d{3}-[\dXx](?!\d)")
_TITULO_BARE = re.compile(r"(?<![\d.])\d{12}(?![\d.])")


def _only_digits(value: str) -> str:
    return re.sub(r"\D", "", value)


def _has_single_repeated_digit(digits: str) -> bool:
    return len(set(digits)) == 1


def is_valid_cpf(value: str) -> bool:
    """Mesmo algoritmo de `Cpf.cs`: módulo 11 com pesos decrescentes, dois dígitos verificadores."""
    digits = _only_digits(value)
    if len(digits) != 11 or _has_single_repeated_digit(digits):
        return False

    def check_digit(base: str) -> int:
        total = sum(int(digit) * weight for digit, weight in zip(base, range(len(base) + 1, 1, -1)))
        remainder = total * 10 % 11
        return 0 if remainder == 10 else remainder

    if check_digit(digits[:9]) != int(digits[9]):
        return False
    return check_digit(digits[:10]) == int(digits[10])


def is_valid_cnpj(value: str) -> bool:
    """Mesmo algoritmo de `Cnpj.cs`, restrito ao formato numérico legado (sem letras)."""
    digits = _only_digits(value)
    if len(digits) != 14 or _has_single_repeated_digit(digits[:12]):
        return False

    first_weights = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]
    second_weights = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]

    def check_digit(base: str, weights: list[int]) -> int:
        total = sum(int(digit) * weight for digit, weight in zip(base, weights))
        remainder = total % 11
        return 0 if remainder < 2 else 11 - remainder

    if check_digit(digits[:12], first_weights) != int(digits[12]):
        return False
    return check_digit(digits[:13], second_weights) == int(digits[13])


def is_valid_cnh_registration(value: str) -> bool:
    """Mesmo algoritmo de `CnhRegistration.cs` (DENATRAN, via brdoc `IsCNH`)."""
    digits = _only_digits(value)
    if len(digits) != 11 or _has_single_repeated_digit(digits):
        return False

    base = digits[:9]
    descending = sum(int(digit) * (9 - index) for index, digit in enumerate(base))
    ascending = sum(int(digit) * (index + 1) for index, digit in enumerate(base))

    first = descending % 11
    discount = 2 if first == 10 else 0
    if first > 9:
        first = 0

    second = (ascending % 11) - discount
    if second < 0:
        second += 11
    if second > 9:
        second = 0

    return f"{first}{second}" == digits[9:]


def is_valid_titulo_eleitor(value: str) -> bool:
    """Algoritmo do TSE (sequencial de 8 dígitos + UF de 2 + 2 verificadores), com a exceção de SP/MG.

    A PoC não tem acesso ao TSE para conferir contra a base oficial: esta é a regra publicada e a que os
    validadores de mercado reproduzem, no mesmo espírito da ressalva já registrada em `CnhRegistration.cs`.
    """
    digits = _only_digits(value)
    if len(digits) != 12 or _has_single_repeated_digit(digits):
        return False

    sequential, uf, dv1_given, dv2_given = digits[:8], digits[8:10], digits[10], digits[11]
    uf_code = int(uf)
    if not 1 <= uf_code <= 28:
        return False

    weights1 = [2, 3, 4, 5, 6, 7, 8, 9]
    sum1 = sum(int(digit) * weight for digit, weight in zip(sequential, weights1))
    remainder1 = sum1 % 11
    dv1 = 0 if remainder1 == 10 else remainder1
    if uf_code in (1, 2) and remainder1 == 0:
        dv1 = 1

    sum2 = int(uf[0]) * 7 + int(uf[1]) * 8 + dv1 * 9
    remainder2 = sum2 % 11
    dv2 = 0 if remainder2 == 10 else remainder2
    if uf_code in (1, 2) and remainder2 == 0:
        dv2 = 1

    return dv1 == int(dv1_given) and dv2 == int(dv2_given)


def _fold(text: str) -> str:
    decomposed = unicodedata.normalize("NFKD", text)
    return "".join(c for c in decomposed if not unicodedata.combining(c)).upper()


@dataclass(frozen=True)
class Finding:
    path: str
    line: int
    kind: str
    redacted: str

    def __str__(self) -> str:
        return f"{self.path}:{self.line}: {self.kind} suspeito ({self.redacted})"


def _redact(digits: str, visible: int = 3) -> str:
    return digits[:visible] + "*" * (len(digits) - visible)


def _scan_line(path: str, line_number: int, line: str, folded_names: list[tuple[str, str]]) -> list[Finding]:
    findings: list[Finding] = []

    # CPF e CNH compartilham o comprimento (11 dígitos): um mesmo achado é checado contra os dois
    # algoritmos e contra a união das duas listas, para não duplicar nem perder um valor sintético que
    # coincidentemente fecha checksum nos dois (ver comentário de _SYNTHETIC_ELEVEN_DIGIT).
    for regex in (_CPF_FORMATTED, _CPF_BARE):
        for match in regex.finditer(line):
            digits = _only_digits(match.group(0))
            if digits in _SYNTHETIC_ELEVEN_DIGIT:
                continue
            if is_valid_cpf(digits):
                findings.append(Finding(path, line_number, "CPF", _redact(digits)))
            elif is_valid_cnh_registration(digits):
                findings.append(Finding(path, line_number, "registro de CNH", _redact(digits)))

    for regex, validator, synthetic, kind in (
        (_CNPJ_FORMATTED, is_valid_cnpj, SYNTHETIC_CNPJS, "CNPJ"),
        (_CNPJ_BARE, is_valid_cnpj, SYNTHETIC_CNPJS, "CNPJ"),
        (_TITULO_BARE, is_valid_titulo_eleitor, SYNTHETIC_TITULOS, "titulo de eleitor"),
    ):
        for match in regex.finditer(line):
            raw = match.group(0)
            digits = _only_digits(raw)
            if not validator(digits):
                continue
            if digits in synthetic:
                continue
            findings.append(Finding(path, line_number, kind, _redact(digits)))

    for match in _CEP_FORMATTED.finditer(line):
        digits = _only_digits(match.group(0))
        if digits not in SYNTHETIC_CEPS:
            findings.append(Finding(path, line_number, "CEP", _redact(digits)))

    for match in _RG_FORMATTED.finditer(line):
        raw = match.group(0)
        normalized = re.sub(r"[.\-]", "", raw).upper()
        if normalized not in SYNTHETIC_RGS:
            findings.append(Finding(path, line_number, "RG", _redact(normalized)))

    if folded_names:
        folded_line = _fold(line)
        for original, folded_name in folded_names:
            if folded_name in folded_line:
                findings.append(Finding(path, line_number, "nome real", f"contem '{original[:2]}...'"))

    return findings


def load_names_file(names_file: Path | None) -> list[tuple[str, str]]:
    """Lê nomes reais (um por linha, `#` comenta) de um arquivo fora do repositório.

    Devolve pares (nome original, nome dobrado para comparação sem acento/caixa). Nunca lança se o
    arquivo não existir: quem chama decide como avisar.
    """
    if names_file is None or not names_file.is_file():
        return []

    names: list[tuple[str, str]] = []
    for raw_line in names_file.read_text(encoding="utf-8").splitlines():
        stripped = raw_line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        names.append((stripped, _fold(stripped)))
    return names


def _is_binary(path: Path) -> bool:
    if path.suffix.lower() in _BINARY_EXTENSIONS:
        return True
    try:
        with path.open("rb") as handle:
            chunk = handle.read(4096)
    except OSError:
        return True
    return b"\0" in chunk


def tracked_files() -> list[Path]:
    result = subprocess.run(
        ["git", "ls-files"],
        cwd=REPO_ROOT,
        capture_output=True,
        text=True,
        check=True,
    )
    return [REPO_ROOT / line for line in result.stdout.splitlines() if line]


def scan_paths(paths: Iterable[Path], names_file: Path | None = None) -> list[Finding]:
    folded_names = load_names_file(names_file)
    findings: list[Finding] = []

    for path in paths:
        if not path.is_file() or _is_binary(path):
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue

        try:
            display_path = str(path.relative_to(REPO_ROOT))
        except ValueError:
            display_path = str(path)

        for line_number, line in enumerate(text.splitlines(), start=1):
            findings.extend(_scan_line(display_path, line_number, line, folded_names))

    return findings


def scan_repository(names_file: Path | None = None) -> list[Finding]:
    return scan_paths(tracked_files(), names_file=names_file)


def main(argv: list[str] | None = None) -> int:
    import argparse
    import os

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--names-file",
        type=Path,
        default=Path(os.environ["DOCREADER_PII_NAMES_FILE"]) if os.environ.get("DOCREADER_PII_NAMES_FILE") else None,
        help="Arquivo fora do repositório com nomes reais a procurar, um por linha (padrão: "
        "variável de ambiente DOCREADER_PII_NAMES_FILE).",
    )
    args = parser.parse_args(argv)

    if args.names_file is None:
        print("aviso: DOCREADER_PII_NAMES_FILE não definida — pulando checagem de nomes reais", file=sys.stderr)
    elif not args.names_file.is_file():
        print(f"aviso: arquivo de nomes '{args.names_file}' não encontrado — pulando checagem de nomes reais", file=sys.stderr)

    findings = scan_repository(names_file=args.names_file)

    if not findings:
        print("varredura de PII: nenhum vazamento encontrado")
        return 0

    print(f"varredura de PII: {len(findings)} achado(s) suspeito(s)", file=sys.stderr)
    for finding in findings:
        print(f"  {finding}", file=sys.stderr)
    print(
        "\nSe for um valor sintético novo, calcule o dígito verificador e adicione a "
        "scripts/pii/synthetic_values.py. Se for dado real, remova do arquivo e use "
        "scripts/pii/mask_ocr_fixture.py para gerar a fixture mascarada.",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
