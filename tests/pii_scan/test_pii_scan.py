"""Testes da varredura de vazamento de PII (`scripts/pii/scan.py`).

Roda com `python -m pytest tests/pii_scan -q`, mesmo estilo leve de `tests/accuracy` (só biblioteca
padrão + pytest, sem docker nem serviço externo).
"""

from __future__ import annotations

import os
import sys
import warnings
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "pii"))

import scan  # noqa: E402


def test_repositorio_atual_nao_tem_vazamento_de_pii() -> None:
    names_file_env = os.environ.get("DOCREADER_PII_NAMES_FILE")
    names_file = Path(names_file_env) if names_file_env else None

    if names_file is None:
        warnings.warn(
            "DOCREADER_PII_NAMES_FILE não definida: a checagem de nomes reais de documentos desta sessão "
            "foi pulada. Defina a variável apontando para um arquivo fora do repositório (um nome por "
            "linha) para cobrir essa parte da varredura.",
            stacklevel=1,
        )
    elif not names_file.is_file():
        warnings.warn(
            f"DOCREADER_PII_NAMES_FILE aponta para '{names_file}', que não existe: checagem de nomes "
            "reais pulada.",
            stacklevel=1,
        )

    findings = scan.scan_repository(names_file=names_file)

    assert not findings, "vazamento de PII encontrado:\n" + "\n".join(str(finding) for finding in findings)


def test_autoteste_varredura_detecta_cpf_real_nao_sintetico(tmp_path: Path) -> None:
    # CPF com dígito verificador válido, calculado especificamente para este autoteste (base 987.654.321,
    # nunca usado em nenhuma fixture do projeto) - não é sintético conhecido, então precisa ser achado.
    base = "987654321"

    real_looking_cpf = None
    for first in range(10):
        for second in range(10):
            candidate = f"{base}{first}{second}"
            if scan.is_valid_cpf(candidate):
                real_looking_cpf = candidate
                break
        if real_looking_cpf:
            break
    assert real_looking_cpf is not None, "não foi possível derivar um CPF de autoteste com checksum válido"
    assert real_looking_cpf not in scan.SYNTHETIC_CPFS

    formatted = f"{real_looking_cpf[:3]}.{real_looking_cpf[3:6]}.{real_looking_cpf[6:9]}-{real_looking_cpf[9:]}"
    probe_file = tmp_path / "arquivo-com-cpf-real.txt"
    probe_file.write_text(f"Nome: Fulano de Autoteste\nCPF: {formatted}\n", encoding="utf-8")

    findings = scan.scan_paths([probe_file])

    assert len(findings) == 1
    assert findings[0].kind == "CPF"


def test_autoteste_varredura_nao_acha_cpf_sintetico_conhecido(tmp_path: Path) -> None:
    probe_file = tmp_path / "arquivo-com-cpf-sintetico.txt"
    probe_file.write_text("CPF: 111.444.777-35\n", encoding="utf-8")

    findings = scan.scan_paths([probe_file])

    assert findings == []


def test_autoteste_varredura_detecta_nome_real_quando_arquivo_de_nomes_e_passado(tmp_path: Path) -> None:
    names_file = tmp_path / "nomes-reais.txt"
    names_file.write_text("# comentário\nFulano de Autoteste da Silva\n", encoding="utf-8")

    probe_file = tmp_path / "documento.txt"
    probe_file.write_text("Texto qualquer com FULANO DE AUTOTESTE DA SILVA no meio.\n", encoding="utf-8")

    findings = scan.scan_paths([probe_file], names_file=names_file)

    assert len(findings) == 1
    assert findings[0].kind == "nome real"


@pytest.mark.parametrize(
    "value",
    [
        "111.444.777-35",  # CPF sintético canônico do projeto
        "04.252.011/0001-10",  # CNPJ sintético canônico do projeto
    ],
)
def test_validadores_aceitam_os_sinteticos_canonicos(value: str) -> None:
    if "/" in value:
        assert scan.is_valid_cnpj(value) is True
    else:
        assert scan.is_valid_cpf(value) is True
