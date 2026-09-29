"""Fonte única dos valores sintéticos (falsos, de propósito) já usados no projeto.

Todo CPF, CNPJ, CEP, número de registro de CNH ou RG com dígito verificador válido (ou, para RG/CEP, que
não tem dígito verificador nacional, só de formato) que aparece em `scan.py` como "encontrado" precisa
estar aqui para não falhar a varredura. A lista foi construída auditando o repositório inteiro nesta
rodada: todo valor abaixo já era usado deliberadamente como dado de teste antes desta auditoria, verificado
um a um contra o algoritmo de dígito verificador real (`scripts/pii/scan.py`, mesmas contas de
`src/DocReader.Domain/Validation/Cpf.cs`/`Cnpj.cs`/`CnhRegistration.cs`).

Adicionar um valor aqui é uma decisão deliberada: só faça isso para um valor sintético novo (calculado,
nunca copiado de um documento real). Ver `docs/bench/real-exploratory-v1.md` e o CLAUDE.md sobre os dois
vazamentos que motivaram este scanner.
"""

from __future__ import annotations

# CPF (11 dígitos, sem máscara). "11144477735" e "52998224725" são os exemplos clássicos usados em
# tutoriais brasileiros de validação de CPF (por isso aparecem em dezenas de arquivos); os demais foram
# derivados nesta sessão ou em rodadas anteriores especificamente para os testes que os usam.
SYNTHETIC_CPFS: frozenset[str] = frozenset(
    {
        "11144477735",
        "52998224725",
        "11122233396",
        "11133355560",
        "12345678909",
        "01234567890",  # exemplo em schemas/documents/BR_CNH.v1.json, sequencial de propósito
    }
)

# CNPJ (14 dígitos, sem máscara, formato numérico legado).
SYNTHETIC_CNPJS: frozenset[str] = frozenset(
    {
        "04252011000110",
        "11222333000181",
    }
)

# Número de registro de CNH (11 dígitos, algoritmo do DENATRAN via CnhRegistration.cs). Os vetores de
# CnhRegistrationTests.cs vêm do docstring da classe: "calculados por uma transcrição independente do
# IsCNH do brdoc", cobrindo os três caminhos do segundo dígito verificador.
SYNTHETIC_CNH_REGISTRATIONS: frozenset[str] = frozenset(
    {
        "04512345621",
        "02650306461",
        "12345678900",
        "00000000778",
        "00000001460",
        "00000018200",
        "00000033609",
        "00000084009",
        "00000004204",
        "00000007707",
        "00000030106",
        "00000105708",
        "00000156108",
        "00000199508",
    }
)

# CEP (8 dígitos, sem máscara). Não existe dígito verificador nacional de CEP: a lista é de formato, não
# de checksum. "13053024" (13053-024, Campinas/SP) é o CEP público da própria concessionária Elektro,
# impresso em toda nota fiscal que ela emite — dado empresarial público, não endereço de uma pessoa —
# retido de propósito na fixture `elektro-real.ocr.json`.
SYNTHETIC_CEPS: frozenset[str] = frozenset(
    {
        "00000000",
        "01000000",
        "04000000",
        "05000000",
        "09999000",
        "11111000",
        "11111070",
        "11111111",
        "11111450",
        "13000000",
        "13053024",
        "22222222",
        "60924000",
        "60924100",
        "60924999",
        "60925000",
        "60926999",
        "60928000",
        "60929000",
        "61332000",
        "99999999",
    }
)

# RG (formato varia por estado; não existe dígito verificador nacional). Valores de formato
# XX.XXX.XXX-D (D dígito ou X), normalizados sem pontuação/máscara.
SYNTHETIC_RGS: frozenset[str] = frozenset(
    {
        "114447773",
        "123456789",
        "114447779",
        "12345678X",
        "301234567",
        "234567893",
        "234567890",
        "11111111",
    }
)

# Título de eleitor (12 dígitos, algoritmo do TSE). Nenhum valor sintético usado ainda no repositório.
SYNTHETIC_TITULOS: frozenset[str] = frozenset()
