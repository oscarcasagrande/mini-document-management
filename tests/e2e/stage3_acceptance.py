"""Teste ponta a ponta da Etapa 3: envia uma amostra de cada tipo pela API, espera COMPLETED e confere
tipo, campos, validação e a tela de detalhe.

Roda contra o compose de pé, sem dependência além da biblioteca padrão:

    docker compose up -d --build
    docker run --rm -v "$PWD:/w" -w /w python:3.12-slim \
      python tests/e2e/stage3_acceptance.py --api http://host.docker.internal:8080 --web http://host.docker.internal:3000

O que cada amostra deve devolver está em samples/synthetic/documents/<amostra>.expected.json, o mesmo
arquivo que os testes unitários leem. Sai com código 0 só se todos os tipos passarem.
"""

from __future__ import annotations

import argparse
import html
import json
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
SAMPLES = ROOT / "samples" / "synthetic" / "documents"

# nome da amostra, arquivo, MIME
CASES = [
    ("cin-frente-verso", "cin-frente-verso.png", "image/png"),
    ("cnh", "cnh.png", "image/png"),
    ("cnh-vencida", "cnh-vencida.png", "image/png"),
    ("comprovante-residencia", "comprovante-residencia.png", "image/png"),
    ("cartao-cnpj", "cartao-cnpj.png", "image/png"),
    ("ccmei", "ccmei.png", "image/png"),
    ("contrato-social", "contrato-social.pdf", "application/pdf"),
]

TERMINAL = {"COMPLETED", "FAILED", "REJECTED"}


def request(url: str, *, method: str = "GET", body: bytes | None = None, headers: dict | None = None) -> tuple[int, bytes, dict]:
    req = urllib.request.Request(url, data=body, headers=headers or {}, method=method)
    try:
        with urllib.request.urlopen(req, timeout=60) as response:
            return response.status, response.read(), dict(response.headers)
    except urllib.error.HTTPError as error:
        return error.code, error.read(), dict(error.headers)


def upload(api: str, path: Path, mime: str, expected_type: str) -> dict:
    boundary = uuid.uuid4().hex
    parts = [
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"expectedDocumentType\"\r\n\r\n{expected_type}\r\n".encode(),
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"channel\"\r\n\r\nAPI\r\n".encode(),
        (
            f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{path.name}\"\r\n"
            f"Content-Type: {mime}\r\n\r\n"
        ).encode()
        + path.read_bytes()
        + b"\r\n",
        f"--{boundary}--\r\n".encode(),
    ]
    status, body, headers = request(
        f"{api}/api/v1/documents",
        method="POST",
        body=b"".join(parts),
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}", "Idempotency-Key": uuid.uuid4().hex},
    )
    if status != 202:
        raise AssertionError(f"upload devolveu {status}: {body[:300]!r}")
    if "Location" not in headers:
        raise AssertionError("upload sem header Location")
    return json.loads(body)


def wait_for_terminal(api: str, document_id: str, timeout_seconds: int) -> dict:
    deadline = time.monotonic() + timeout_seconds
    last = None
    while time.monotonic() < deadline:
        status, body, _ = request(f"{api}/api/v1/documents/{document_id}/status")
        if status != 200:
            raise AssertionError(f"status devolveu {status}")
        last = json.loads(body)
        if last["status"] in TERMINAL:
            return last
        time.sleep(2)
    raise AssertionError(f"não terminou em {timeout_seconds}s; último status: {last and last['status']}")


def check_case(api: str, web: str | None, name: str, filename: str, mime: str, timeout_seconds: int) -> list[str]:
    expected = json.loads((SAMPLES / f"{name}.expected.json").read_text(encoding="utf-8"))
    problems: list[str] = []
    started = time.monotonic()

    created = upload(api, SAMPLES / filename, mime, expected["documentType"])
    document_id = created["id"]
    upload_seconds = time.monotonic() - started
    final = wait_for_terminal(api, document_id, timeout_seconds)
    elapsed = time.monotonic() - started

    if final["status"] != "COMPLETED":
        return [f"terminou em {final['status']} em vez de COMPLETED"]

    status, body, _ = request(f"{api}/api/v1/documents/{document_id}/result")
    if status != 200:
        return [f"/result devolveu {status}"]
    result = json.loads(body)

    detected = result["classification"]["detectedType"]
    if detected != expected["documentType"]:
        problems.append(f"tipo detectado {detected}, esperado {expected['documentType']}")

    fields = result["extraction"]["fields"]
    for path, want in expected["fields"].items():
        got = fields.get(path)
        if got is None:
            problems.append(f"{path}: ausente do resultado")
            continue
        if got["validationStatus"] != want["status"] or got.get("normalized") != want["normalized"]:
            problems.append(
                f"{path}: esperado {want['normalized']!r} ({want['status']}), "
                f"veio {got.get('normalized')!r} ({got['validationStatus']}, lido {got.get('raw')!r})"
            )

        missing = [code for code in want.get("messages", []) if code not in (got.get("validationMessages") or [])]
        if missing:
            problems.append(f"{path}: faltam os códigos {', '.join(missing)}")

    # Nenhum campo fora do esperado com status VALID sem valor normalizado: seria um campo mal formado.
    for path, got in fields.items():
        if got["validationStatus"] == "VALID" and got.get("normalized") is None:
            problems.append(f"{path}: VALID sem valor normalizado")

    status, _, _ = request(f"{api}/api/v1/documents/{document_id}/text")
    if status != 200:
        problems.append(f"/text devolveu {status}")

    if web:
        status, page, _ = request(f"{web}/documents/{document_id}")
        # A tela é HTML: "<" e "&" chegam escapados. Compara o texto já desescapado, e só a primeira linha
        # de valores de várias linhas (a MRZ), que o HTML mostra em células com quebra.
        text = html.unescape(page.decode("utf-8", errors="replace"))
        if status != 200:
            problems.append(f"tela de detalhe devolveu {status}")
        else:
            for path, want in expected["fields"].items():
                if want["normalized"] and want["status"] == "VALID":
                    raw = fields.get(path, {}).get("raw")
                    first_line = raw.splitlines()[0] if raw else None
                    if first_line and first_line not in text:
                        problems.append(f"tela de detalhe não mostra o valor lido de {path}: {first_line!r}")

    print(
        f"  {name:<24} {final['status']} em {elapsed:5.1f}s (upload {upload_seconds:.2f}s)  tipo={detected} "
        f"confiança={result['extraction'].get('overallConfidence')}",
        flush=True,
    )
    print(f"      detalhe: {web or api}/documents/{document_id}", flush=True)
    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description="Aceite ponta a ponta da Etapa 3")
    parser.add_argument("--api", default="http://localhost:8080")
    parser.add_argument("--web", default=None, help="Endereço da UI para conferir a tela de detalhe")
    parser.add_argument("--timeout", type=int, default=420, help="Segundos de espera por documento")
    parser.add_argument("--only", help="Amostras separadas por vírgula")
    args = parser.parse_args()

    wanted = {name.strip() for name in args.only.split(",")} if args.only else None
    failures = 0

    print(f"Aceite da Etapa 3 em {args.api}")
    for name, filename, mime in CASES:
        if wanted and name not in wanted:
            continue
        try:
            problems = check_case(args.api, args.web, name, filename, mime, args.timeout)
        except AssertionError as error:
            problems = [str(error)]

        if problems:
            failures += 1
            print(f"  {name:<24} FALHOU")
            for problem in problems:
                print(f"      - {problem}")

    print()
    print("Todos os tipos passaram." if failures == 0 else f"{failures} tipo(s) falharam.")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
