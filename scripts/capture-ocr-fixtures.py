"""Captura o que o ocr-service devolve para as amostras sintéticas da Etapa 3, para usar como fixture.

Os testes unitários dos extratores rodam sobre o OCR real (texto, ordem de leitura, confiança e
coordenadas), não sobre linhas escritas à mão: foi assim que a Etapa 2 achou o erro da data de inscrição
do cartão de CPF. As fixtures ficam versionadas em tests/unit/DocReader.UnitTests/Fixtures/ocr/, e este
script as regenera quando a engine, o pré-processamento ou as amostras mudam.

Só biblioteca padrão. O ocr-service não publica porta no compose de aceite; rode dentro da rede do compose:

    docker run --rm --network docreader_internal -v "$PWD:/w" -w /w python:3.12-slim \
      python scripts/capture-ocr-fixtures.py --base-url http://ocr-service:8000

Saída: uma <amostra>.ocr.json por arquivo, no formato
  {"source": "...", "modelVersion": "...", "pages": [{"page": 1, "imageWidth": ..., "blocks": [...]}]}
"""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
import urllib.request
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_SAMPLES = ROOT / "samples" / "synthetic" / "documents"
DEFAULT_OUTPUT = ROOT / "tests" / "unit" / "DocReader.UnitTests" / "Fixtures" / "ocr"

MIME = {".png": "image/png", ".pdf": "application/pdf", ".jpg": "image/jpeg", ".jpeg": "image/jpeg"}


def post_page(base_url: str, name: str, content: bytes, mime: str, page: int) -> dict:
    boundary = uuid.uuid4().hex
    parts = [
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"page\"\r\n\r\n{page}\r\n".encode(),
        (
            f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"original\"\r\n"
            f"Content-Type: {mime}\r\n\r\n"
        ).encode()
        + content
        + b"\r\n",
        f"--{boundary}--\r\n".encode(),
    ]
    request = urllib.request.Request(
        f"{base_url}/v1/ocr/page",
        data=b"".join(parts),
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
        method="POST",
    )

    try:
        with urllib.request.urlopen(request, timeout=300) as response:
            return json.loads(response.read())
    except urllib.error.HTTPError as error:
        raise SystemExit(f"{name} página {page}: HTTP {error.code} {error.read()[:200]!r}") from error


def main() -> int:
    parser = argparse.ArgumentParser(description="Captura fixtures de OCR das amostras")
    parser.add_argument("--base-url", default="http://ocr-service:8000")
    parser.add_argument("--samples", type=Path, default=DEFAULT_SAMPLES)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--only", help="Nomes de amostra separados por vírgula (sem extensão)")
    args = parser.parse_args()

    wanted = {name.strip() for name in args.only.split(",")} if args.only else None
    files = sorted(path for path in args.samples.iterdir() if path.suffix.lower() in MIME)
    if wanted:
        files = [path for path in files if path.stem in wanted]
    if not files:
        print(f"nenhuma amostra em {args.samples}", file=sys.stderr)
        return 1

    args.out.mkdir(parents=True, exist_ok=True)

    for path in files:
        content = path.read_bytes()
        pages: list[dict] = []
        model_version = ""
        page_count = 1
        page = 1

        while page <= page_count:
            answer = post_page(args.base_url, path.name, content, MIME[path.suffix.lower()], page)
            page_count = answer["pageCount"]
            model_version = answer["modelVersion"]
            pages.append(
                {
                    "page": answer["page"],
                    "imageWidth": answer["imageWidth"],
                    "imageHeight": answer["imageHeight"],
                    "durationMs": answer["durationMs"],
                    "blocks": [
                        {"text": block["text"], "confidence": block["confidence"], "boundingBox": block["boundingBox"]}
                        for block in answer["blocks"]
                    ],
                }
            )
            print(f"{path.name} página {page}/{page_count}: {len(answer['blocks'])} blocos em {answer['durationMs']} ms", flush=True)
            page += 1

        target = args.out / f"{path.stem}.ocr.json"
        target.write_text(
            json.dumps({"source": path.name, "modelVersion": model_version, "pages": pages}, ensure_ascii=False, indent=1) + "\n",
            encoding="utf-8",
        )
        print(f"  -> {target}", flush=True)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
