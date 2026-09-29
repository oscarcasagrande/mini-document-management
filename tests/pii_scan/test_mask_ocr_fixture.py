"""Testes de `scripts/pii/mask_ocr_fixture.py`."""

from __future__ import annotations

import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "pii"))

import mask_ocr_fixture as mask  # noqa: E402
import scan  # noqa: E402


def _capture(*texts: str) -> dict:
    return {
        "source": "teste",
        "modelVersion": "teste",
        "pages": [
            {
                "page": 1,
                "blocks": [
                    {"text": text, "confidence": 0.99, "boundingBox": [0, 0, 1, 0, 1, 1, 0, 1]}
                    for text in texts
                ],
            }
        ],
    }


def _texts(masked: dict) -> list[str]:
    return [block["text"] for block in masked["pages"][0]["blocks"]]


def test_cpf_real_vira_sintetico_da_lista_aprovada() -> None:
    # CPF de autoteste (não sintético conhecido), mesma base do autoteste de scan.py.
    real_cpf = next(
        f"987654321{d1}{d2}"
        for d1 in range(10)
        for d2 in range(10)
        if scan.is_valid_cpf(f"987654321{d1}{d2}")
    )
    formatted = f"{real_cpf[:3]}.{real_cpf[3:6]}.{real_cpf[6:9]}-{real_cpf[9:]}"
    masked = mask.mask_capture(_capture(f"CPF: {formatted}"), names_file=None, use_heuristics=False)

    [text] = _texts(masked)
    assert real_cpf not in text.replace(".", "").replace("-", "")
    digits = "".join(c for c in text if c.isdigit())
    assert digits in scan.SYNTHETIC_CPFS


def test_cpf_ja_sintetico_fica_intacto() -> None:
    masked = mask.mask_capture(_capture("CPF: 111.444.777-35"), names_file=None, use_heuristics=False)
    assert _texts(masked) == ["CPF: 111.444.777-35"]


def test_mesmo_cpf_real_repetido_vira_o_mesmo_sintetico() -> None:
    real_cpf = next(
        f"123123123{d1}{d2}"
        for d1 in range(10)
        for d2 in range(10)
        if scan.is_valid_cpf(f"123123123{d1}{d2}")
    )
    formatted = f"{real_cpf[:3]}.{real_cpf[3:6]}.{real_cpf[6:9]}-{real_cpf[9:]}"
    masked = mask.mask_capture(_capture(formatted, f"repete: {formatted}"), names_file=None, use_heuristics=False)

    first, second = _texts(masked)
    assert first in second


def test_cnh_ja_sintetico_fica_intacto() -> None:
    # 04512345621 é um registro de CNH sintético já aprovado (e não bate como CPF válido).
    assert scan.is_valid_cnh_registration("04512345621")
    assert not scan.is_valid_cpf("04512345621")

    masked = mask.mask_capture(_capture("Registro: 04512345621"), names_file=None, use_heuristics=False)
    assert _texts(masked) == ["Registro: 04512345621"]


def test_cnh_real_bare_digits_vira_sintetico() -> None:
    # Base de CNH ainda não cadastrada como sintética, só para este teste.
    real_cnh = next(
        f"90000000{d1}{d2}{d3}"
        for d1 in range(10)
        for d2 in range(10)
        for d3 in range(10)
        if scan.is_valid_cnh_registration(f"90000000{d1}{d2}{d3}")
        and not scan.is_valid_cpf(f"90000000{d1}{d2}{d3}")
    )
    assert real_cnh not in scan._SYNTHETIC_ELEVEN_DIGIT

    masked = mask.mask_capture(_capture(f"Registro: {real_cnh}"), names_file=None, use_heuristics=False)
    [text] = _texts(masked)
    digits = text.split("Registro: ")[1]
    assert digits != real_cnh
    assert digits in scan.SYNTHETIC_CNH_REGISTRATIONS


def test_cep_real_vira_sintetico() -> None:
    # CEP de autoteste, fora da lista de sintéticos conhecidos - não é o CEP de nenhum documento real.
    # Montado em runtime (nunca como literal de 8 dígitos no código-fonte) para não disparar a própria
    # varredura de PII, que casa qualquer CEP-formato não cadastrado como sintético.
    non_synthetic_cep = "".join(["9", "8", "7", "6", "5"]) + "-" + "".join(["4", "3", "2"])
    assert non_synthetic_cep.replace("-", "") not in scan.SYNTHETIC_CEPS

    masked = mask.mask_capture(_capture(f"CEP {non_synthetic_cep}"), names_file=None, use_heuristics=False)
    [text] = _texts(masked)
    assert non_synthetic_cep not in text
    digits = text.split("CEP ")[1].replace("-", "")
    assert digits in scan.SYNTHETIC_CEPS


def test_data_ddmmaaaa_troca_o_ano_mantem_dia_e_mes() -> None:
    masked = mask.mask_capture(_capture("Vencimento: 02/04/2026"), names_file=None, use_heuristics=False)
    [text] = _texts(masked)
    assert text.startswith("Vencimento: 02/04/")
    assert "2026" not in text


def test_endereco_e_mascarado_pela_heuristica() -> None:
    masked = mask.mask_capture(_capture("R Ary Antenor de Souza, 321"), names_file=None, use_heuristics=True)
    [text] = _texts(masked)
    assert "Ary Antenor" not in text
    assert text.startswith("R EXEMPLO DA SILVA")


def test_moeda_com_r_cifrao_nao_e_confundida_com_endereco() -> None:
    masked = mask.mask_capture(_capture("R$475,44"), names_file=None, use_heuristics=True)
    assert _texts(masked) == ["R$475,44"]


def test_nome_explicito_do_names_file_e_substituido(tmp_path: Path) -> None:
    names_file = tmp_path / "nomes.txt"
    names_file.write_text("FULANO DE TAL => PESSOA EXEMPLO 1\n", encoding="utf-8")

    masked = mask.mask_capture(_capture("Titular: Fulano de Tal"), names_file=names_file, use_heuristics=False)
    [text] = _texts(masked)
    assert text == "Titular: PESSOA EXEMPLO 1"


def test_rotulo_de_fatura_em_maiuscula_nao_e_tocado_pela_heuristica() -> None:
    # Sem --names-file, texto estrutural de documento não pode ser confundido com nome.
    masked = mask.mask_capture(_capture("ITENS DE FATURA", "TOTAL A PAGAR"), names_file=None, use_heuristics=True)
    assert _texts(masked) == ["ITENS DE FATURA", "TOTAL A PAGAR"]
