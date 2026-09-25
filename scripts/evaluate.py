#!/usr/bin/env python3
"""Avaliação do DocReader contra uma pasta de documentos anotados (Etapa 4, PRD §23 e §26).

Para cada documento da pasta que tenha ground truth, o script o envia à API, espera o processamento, lê o
resultado e compara com a anotação. Gera precision, recall e F1 por tipo e por campo, exatidão de dígito
verificador, acerto de classificação, rendimento de texto e latência, mais os três indicadores da PoC
(PRD §3): classificação >= 85%, texto utilizável >= 90% e campos críticos com exact match >= 90%.

    python scripts/evaluate.py --dataset /caminho/fora/do/repo --api http://localhost:8080

O formato do ground truth e a definição de cada métrica estão em docs/evaluation.md. Só biblioteca padrão.

Sobre dados reais: o script recusa uma pasta de documentos dentro deste repositório (salvo samples/synthetic),
não escreve valores de campo no relatório a menos que se peça, e apaga da API os documentos que enviou.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import statistics
import sys
import time
import urllib.error
import urllib.request
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parent.parent
SYNTHETIC_ROOT = REPO_ROOT / "samples" / "synthetic"
SCHEMAS_DIR = REPO_ROOT / "schemas" / "documents"

DOCUMENT_EXTENSIONS = {".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".pdf": "application/pdf", ".tif": "image/tiff", ".tiff": "image/tiff"}
TERMINAL_STATUSES = {"COMPLETED", "FAILED", "REJECTED"}
UNKNOWN_TYPE = "UNKNOWN"

# Os sete tipos com extrator. O indicador de classificação (PRD §3) vale para eles.
PRIORITIZED_TYPES = [
    "BR_CPF_CARD", "BR_CIN", "BR_CNH", "BR_PROOF_OF_ADDRESS", "BR_CNPJ_CARD", "BR_CCMEI", "BR_SOCIAL_CONTRACT",
]

# Campos cujo status depende de um dígito verificador. Para eles, além de acertar o valor, mede-se se o veredito
# do sistema (VALID ou INVALID) concorda com o da anotação.
DV_FIELDS: dict[str, set[str]] = {
    "BR_CPF_CARD": {"cpf"},
    "BR_CIN": {"cpf", "mrz"},
    "BR_CNH": {"cpf", "registrationNumber"},
    "BR_PROOF_OF_ADDRESS": {"holderDocument"},
    "BR_CNPJ_CARD": {"cnpj"},
    "BR_CCMEI": {"cnpj", "holderCpf"},
    "BR_SOCIAL_CONTRACT": {"cnpj", "partners[].cpf"},
}

THRESHOLDS = {"classification": 0.85, "textYield": 0.90, "criticalExactMatch": 0.90}


# ------------------------------------------------------------------------------------------ ground truth

@dataclass(frozen=True)
class ExpectedField:
    """O que a anotação diz de um campo."""

    value: str | None
    status: str  # VALID, INVALID, NOT_FOUND ou UNCERTAIN
    raw: str | None = None
    messages: tuple[str, ...] = ()

    @property
    def is_absent(self) -> bool:
        return self.status == "NOT_FOUND"


@dataclass(frozen=True)
class Truth:
    document_type: str
    quality: str
    legible: bool
    fields: dict[str, ExpectedField]


class TruthError(ValueError):
    """A anotação não está no formato documentado."""


def generic_path(path: str) -> str:
    """`partners[3].cpf` vira `partners[].cpf`: o relatório agrega por campo, não por índice."""
    return re.sub(r"\[\d+\]", "[]", path)


def parse_field(path: str, spec: Any) -> ExpectedField:
    """
    Três formas: `null` (o campo não existe no documento), uma string (o valor esperado) ou um objeto com
    `value` (ou `normalized`, o nome que os `.expected.json` das amostras sintéticas usam), `status`, `raw`
    e `messages`.
    """
    if spec is None:
        return ExpectedField(None, "NOT_FOUND")

    if isinstance(spec, str):
        return ExpectedField(spec, "VALID")

    if not isinstance(spec, dict):
        raise TruthError(f"campo '{path}': esperado null, texto ou objeto")

    value = spec["value"] if "value" in spec else spec.get("normalized")
    status = spec.get("status") or ("VALID" if value is not None else "NOT_FOUND")
    if status not in {"VALID", "INVALID", "NOT_FOUND", "UNCERTAIN"}:
        raise TruthError(f"campo '{path}': status '{status}' inválido")
    if status in {"VALID", "UNCERTAIN"} and value is None:
        raise TruthError(f"campo '{path}': status {status} exige 'value'")
    if status == "NOT_FOUND" and value is not None:
        raise TruthError(f"campo '{path}': NOT_FOUND não tem valor")
    if status == "INVALID" and value is not None:
        raise TruthError(f"campo '{path}': INVALID descreve um valor que o documento traz errado; use 'raw', não 'value'")

    messages = spec.get("messages") or []
    if not isinstance(messages, list) or not all(isinstance(item, str) for item in messages):
        raise TruthError(f"campo '{path}': 'messages' deve ser uma lista de códigos")

    return ExpectedField(value, status, spec.get("raw"), tuple(messages))


def parse_truth(document: dict[str, Any]) -> Truth:
    if not isinstance(document, dict):
        raise TruthError("a anotação deve ser um objeto JSON")

    document_type = document.get("documentType")
    if not isinstance(document_type, str) or not document_type:
        raise TruthError("'documentType' é obrigatório (use UNKNOWN para um documento sem tipo estruturado)")

    fields_spec = document.get("fields", {})
    if not isinstance(fields_spec, dict):
        raise TruthError("'fields' deve ser um objeto")

    return Truth(
        document_type=document_type,
        quality=str(document.get("quality") or "unspecified"),
        legible=bool(document.get("legible", True)),
        fields={path: parse_field(path, spec) for path, spec in fields_spec.items()},
    )


def load_truth(path: Path) -> Truth:
    try:
        # utf-8-sig: o PowerShell e o Bloco de Notas gravam JSON com BOM, e o json do Python não o aceita.
        return parse_truth(json.loads(path.read_text(encoding="utf-8-sig")))
    except json.JSONDecodeError as error:
        raise TruthError(f"JSON inválido: {error}") from error


# ------------------------------------------------------------------------------------------ pontuação

@dataclass
class Counts:
    tp: int = 0
    fp: int = 0
    fn: int = 0
    tn: int = 0
    dv_correct: int = 0
    dv_wrong: int = 0
    dv_no_verdict: int = 0

    def add(self, other: "Counts") -> None:
        for name in ("tp", "fp", "fn", "tn", "dv_correct", "dv_wrong", "dv_no_verdict"):
            setattr(self, name, getattr(self, name) + getattr(other, name))

    @property
    def precision(self) -> float | None:
        return self.tp / (self.tp + self.fp) if (self.tp + self.fp) else None

    @property
    def recall(self) -> float | None:
        return self.tp / (self.tp + self.fn) if (self.tp + self.fn) else None

    @property
    def f1(self) -> float | None:
        p, r = self.precision, self.recall
        if p is None or r is None:
            return None
        return 2 * p * r / (p + r) if (p + r) else 0.0

    @property
    def dv_accuracy(self) -> float | None:
        verdicts = self.dv_correct + self.dv_wrong
        return self.dv_correct / verdicts if verdicts else None

    def as_dict(self) -> dict[str, Any]:
        result: dict[str, Any] = {
            "truePositives": self.tp, "falsePositives": self.fp, "falseNegatives": self.fn, "trueNegatives": self.tn,
            "precision": _round(self.precision), "recall": _round(self.recall), "f1": _round(self.f1),
            # Exact match sobre os campos que o documento traz: é o recall.
            "exactMatch": _round(self.recall),
        }
        if self.dv_correct or self.dv_wrong or self.dv_no_verdict:
            result["checkDigit"] = {
                "verdictsCorrect": self.dv_correct, "verdictsWrong": self.dv_wrong, "noVerdict": self.dv_no_verdict,
                "accuracy": _round(self.dv_accuracy),
            }
        return result


def _round(value: float | None) -> float | None:
    return None if value is None else round(value, 4)


@dataclass(frozen=True)
class FieldScore:
    path: str
    outcome: str  # TP, FP, FN, FP+FN, TN
    counts: Counts
    detail: str
    expected: str | None
    got: str | None


def score_field(document_type: str, path: str, expected: ExpectedField, got: dict[str, Any] | None) -> FieldScore:
    """
    Compara um campo com a anotação.

    - Campo que o documento não traz: NOT_FOUND é acerto (TN); qualquer valor devolvido é falso positivo.
    - Campo que o documento traz com defeito (`INVALID`): acerta quem o devolve como INVALID, com o `raw`
      esperado quando a anotação o dá.
    - Campo com valor: acerta quem devolve o mesmo valor normalizado. Valor diferente é falso positivo e
      falso negativo ao mesmo tempo; nada devolvido, ou INVALID sem valor normalizado, é falso negativo.
    - Para campos com dígito verificador, o veredito do sistema (VALID ou INVALID) é conferido à parte.
    """
    counts = Counts()
    status = (got or {}).get("validationStatus", "NOT_FOUND")
    normalized = (got or {}).get("normalized")
    raw = (got or {}).get("raw")
    got_shown = normalized if normalized is not None else raw

    if expected.is_absent:
        if status == "NOT_FOUND":
            counts.tn = 1
            outcome, detail = "TN", "ausente e não devolvido"
        else:
            counts.fp = 1
            outcome, detail = "FP", "o documento não traz o campo e o sistema devolveu um valor"
    elif expected.status == "INVALID":
        if status == "INVALID" and (expected.raw is None or raw == expected.raw):
            counts.tp = 1
            outcome, detail = "TP", "defeito reconhecido"
        elif status == "NOT_FOUND":
            counts.fn = 1
            outcome, detail = "FN", "o defeito não foi visto: o campo não foi lido"
        else:
            counts.fp = 1
            counts.fn = 1
            outcome, detail = "FP+FN", f"esperado INVALID, veio {status}"
    elif normalized is not None and normalized == expected.value:
        counts.tp = 1
        outcome, detail = "TP", "valor igual"
    elif normalized is not None:
        counts.fp = 1
        counts.fn = 1
        outcome, detail = "FP+FN", "valor diferente do esperado"
    elif status == "INVALID":
        counts.fn = 1
        outcome, detail = "FN", "o sistema reprovou o campo (dígito ou data) e não devolveu valor"
    else:
        counts.fn = 1
        outcome, detail = "FN", "campo não encontrado"

    if generic_path(path) in DV_FIELDS.get(document_type, set()) and not expected.is_absent:
        wanted = "INVALID" if expected.status == "INVALID" else "VALID"
        if status in {"VALID", "INVALID"}:
            if status == wanted:
                counts.dv_correct = 1
            else:
                counts.dv_wrong = 1
        else:
            counts.dv_no_verdict = 1

    missing = [code for code in expected.messages if code not in ((got or {}).get("validationMessages") or [])]
    if missing and outcome == "TP":
        # O valor está certo, mas um aviso esperado (como DOCUMENT_EXPIRED) não veio.
        counts.tp, counts.fn = 0, 1
        outcome, detail = "FN", f"faltam os códigos {', '.join(missing)}"

    return FieldScore(path, outcome, counts, detail, expected.value if expected.value is not None else expected.raw, got_shown)


@dataclass
class DocumentResult:
    """O que o sistema devolveu para um documento (ou por que não devolveu)."""

    file: Path
    truth: Truth
    status: str = "PENDING"  # COMPLETED, FAILED, REJECTED, TIMEOUT, UPLOAD_ERROR
    detected_type: str = "NONE"
    fields: dict[str, dict[str, Any]] = field(default_factory=dict)
    text_chars: int = 0
    elapsed_seconds: float = 0.0
    error: str | None = None


def score_document(result: DocumentResult) -> list[FieldScore]:
    scores: list[FieldScore] = []
    completed = result.status == "COMPLETED"

    for path, expected in result.truth.fields.items():
        if not completed:
            # Documento que não terminou não leu nada: o que existia no papel é falso negativo, e o que não
            # existia não diz nada sobre o sistema.
            if not expected.is_absent:
                counts = Counts(fn=1)
                scores.append(FieldScore(path, "FN", counts, f"documento {result.status}", expected.value, None))
            continue

        scores.append(score_field(result.truth.document_type, path, expected, result.fields.get(path)))

    return scores


# ------------------------------------------------------------------------------------------ agregação

def critical_fields(document_type: str, overrides: dict[str, list[str]] | None = None) -> list[str]:
    """Campos críticos de um tipo: os `required` do schema, ou o que `--critical` sobrescrever."""
    if overrides and document_type in overrides:
        return overrides[document_type]

    schema = SCHEMAS_DIR / f"{document_type}.v1.json"
    if not schema.exists():
        return []

    return list(json.loads(schema.read_text(encoding="utf-8")).get("required", []))


def aggregate(results: list[DocumentResult], overrides: dict[str, list[str]] | None = None) -> dict[str, Any]:
    """Todas as métricas do relatório, a partir dos resultados por documento."""
    types: dict[str, dict[str, Any]] = {}
    per_quality: dict[str, dict[str, Any]] = {}
    global_counts = Counts()

    def bucket(store: dict[str, Any], key: str) -> dict[str, Any]:
        return store.setdefault(key, {"documents": 0, "fields": {}, "micro": Counts(), "critical": Counts(),
                                      "classification": [0, 0]})

    for result in results:
        scores = score_document(result)
        document_type = result.truth.document_type
        critical = set(critical_fields(document_type, overrides))

        for store, key in ((types, document_type), (per_quality, f"{document_type}|{result.truth.quality}")):
            entry = bucket(store, key)
            entry["documents"] += 1
            entry["classification"][1] += 1
            if result.status == "COMPLETED" and result.detected_type == document_type:
                entry["classification"][0] += 1

            for score in scores:
                generic = generic_path(score.path)
                entry["fields"].setdefault(generic, Counts()).add(score.counts)
                entry["micro"].add(score.counts)
                if generic in critical and score.outcome != "TN":
                    entry["critical"].add(score.counts)

        for score in scores:
            global_counts.add(score.counts)

    def render(entry: dict[str, Any]) -> dict[str, Any]:
        hits, total = entry["classification"]
        return {
            "documents": entry["documents"],
            "classification": {"correct": hits, "total": total, "accuracy": _round(hits / total if total else None)},
            "micro": entry["micro"].as_dict(),
            "criticalFields": entry["critical"].as_dict(),
            "fields": {name: counts.as_dict() for name, counts in sorted(entry["fields"].items())},
        }

    return {
        "types": {name: render(entry) for name, entry in sorted(types.items())},
        "quality": {name: render(entry) for name, entry in sorted(per_quality.items())},
        "overall": global_counts.as_dict(),
    }


def classification_report(results: list[DocumentResult]) -> dict[str, Any]:
    """Acerto de classificação e matriz de confusão. O indicador do PRD vale para os tipos priorizados."""
    confusion: dict[str, dict[str, int]] = {}
    correct = 0
    prioritized_total = 0
    prioritized_correct = 0

    for result in results:
        predicted = result.detected_type if result.status == "COMPLETED" else result.status
        confusion.setdefault(result.truth.document_type, {}).setdefault(predicted, 0)
        confusion[result.truth.document_type][predicted] += 1

        hit = predicted == result.truth.document_type
        correct += hit
        if result.truth.document_type in PRIORITIZED_TYPES:
            prioritized_total += 1
            prioritized_correct += hit

    return {
        "accuracy": _round(correct / len(results) if results else None),
        "prioritizedAccuracy": _round(prioritized_correct / prioritized_total if prioritized_total else None),
        "prioritizedDocuments": prioritized_total,
        "confusion": {truth: dict(sorted(row.items())) for truth, row in sorted(confusion.items())},
    }


def text_yield(results: list[DocumentResult], minimum_chars: int) -> dict[str, Any]:
    """Fração dos documentos legíveis que geraram texto utilizável (PRD §3)."""
    legible = [item for item in results if item.truth.legible]
    usable = [item for item in legible if item.status == "COMPLETED" and item.text_chars >= minimum_chars]

    return {
        "legibleDocuments": len(legible),
        "usable": len(usable),
        "rate": _round(len(usable) / len(legible) if legible else None),
        "minimumChars": minimum_chars,
    }


def latency_report(results: list[DocumentResult]) -> dict[str, Any]:
    by_type: dict[str, list[float]] = {}
    for result in results:
        if result.status == "COMPLETED":
            by_type.setdefault(result.truth.document_type, []).append(result.elapsed_seconds)

    return {
        name: {"documents": len(values), "medianSeconds": round(statistics.median(values), 1), "maxSeconds": round(max(values), 1)}
        for name, values in sorted(by_type.items())
    }


def indicators(report: dict[str, Any]) -> list[dict[str, Any]]:
    """Os três indicadores de acurácia da PoC (PRD §3) contra os limiares."""
    critical_total = Counts()
    for entry in report["types"].values():
        block = entry["criticalFields"]
        critical_total.tp += block["truePositives"]
        critical_total.fn += block["falseNegatives"]

    values = {
        "classification": report["classification"]["prioritizedAccuracy"],
        "textYield": report["textYield"]["rate"],
        "criticalExactMatch": _round(critical_total.recall),
    }
    labels = {
        "classification": "Documentos priorizados classificados corretamente",
        "textYield": "Documentos legíveis com texto utilizável",
        "criticalExactMatch": "Campos críticos com exact match",
    }

    return [
        {
            "id": key, "description": labels[key], "threshold": THRESHOLDS[key], "value": value,
            "passed": None if value is None else value >= THRESHOLDS[key],
        }
        for key, value in values.items()
    ]


def compare_with_baseline(current: dict[str, Any], baseline: dict[str, Any], max_drop: float) -> list[str]:
    """Regressões: F1 por tipo e campo que caiu mais que `max_drop`, e campo que existia e sumiu."""
    regressions: list[str] = []

    for document_type, entry in baseline.get("types", {}).items():
        now_entry = current["types"].get(document_type)
        if now_entry is None:
            regressions.append(f"{document_type}: sem documentos nesta rodada")
            continue

        for name, before in entry["fields"].items():
            after = now_entry["fields"].get(name)
            if before.get("f1") is None:
                continue
            if after is None or after.get("f1") is None:
                regressions.append(f"{document_type}.{name}: F1 {before['f1']:.3f} -> sem medição")
            elif before["f1"] - after["f1"] > max_drop:
                regressions.append(f"{document_type}.{name}: F1 {before['f1']:.3f} -> {after['f1']:.3f}")

    return regressions


# ------------------------------------------------------------------------------------------ dados e rede

def check_dataset_location(dataset: Path, allow_inside_repo: bool) -> None:
    """Documentos reais não podem ir para o repositório: recusa uma pasta dentro dele, salvo as amostras."""
    resolved = dataset.resolve()
    try:
        resolved.relative_to(REPO_ROOT)
    except ValueError:
        return

    try:
        resolved.relative_to(SYNTHETIC_ROOT)
        return
    except ValueError:
        pass

    if not allow_inside_repo:
        print(
            f"A pasta {dataset} está dentro do repositório. Documentos reais não podem ficar aqui: aponte para uma "
            f"pasta local fora dele (ou use --allow-inside-repo, sabendo que .gitignore não é uma garantia).",
            file=sys.stderr,
        )
        raise SystemExit(2)


def discover(dataset: Path, truth_suffix: str) -> tuple[list[tuple[Path, Path]], list[Path], list[Path]]:
    """Pares (documento, anotação), documentos sem anotação e anotações sem documento."""
    pairs: list[tuple[Path, Path]] = []
    unannotated: list[Path] = []
    used: set[Path] = set()

    for path in sorted(dataset.rglob("*")):
        if not path.is_file() or path.suffix.lower() not in DOCUMENT_EXTENSIONS:
            continue

        truth = path.with_name(path.stem + truth_suffix)
        if truth.exists():
            pairs.append((path, truth))
            used.add(truth)
        else:
            unannotated.append(path)

    orphans = [path for path in sorted(dataset.rglob(f"*{truth_suffix}")) if path not in used]
    return pairs, unannotated, orphans


class ApiClient:
    def __init__(self, base_url: str) -> None:
        self.base_url = base_url.rstrip("/")

    def request(self, method: str, path: str, body: bytes | None = None, headers: dict[str, str] | None = None) -> tuple[int, bytes, dict[str, str]]:
        request = urllib.request.Request(f"{self.base_url}{path}", data=body, headers=headers or {}, method=method)
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                return response.status, response.read(), dict(response.headers)
        except urllib.error.HTTPError as error:
            return error.code, error.read(), dict(error.headers)

    def upload(self, path: Path) -> dict[str, Any]:
        # Não envia expectedDocumentType: a avaliação mede a classificação sem dica.
        boundary = uuid.uuid4().hex
        payload = b"".join([
            f'--{boundary}\r\nContent-Disposition: form-data; name="channel"\r\n\r\nAPI\r\n'.encode(),
            (
                f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{path.name}"\r\n'
                f"Content-Type: {DOCUMENT_EXTENSIONS[path.suffix.lower()]}\r\n\r\n"
            ).encode() + path.read_bytes() + b"\r\n",
            f"--{boundary}--\r\n".encode(),
        ])
        status, body, _ = self.request(
            "POST", "/api/v1/documents", payload,
            {"Content-Type": f"multipart/form-data; boundary={boundary}", "Idempotency-Key": uuid.uuid4().hex},
        )
        if status != 202:
            raise RuntimeError(f"upload devolveu HTTP {status}")
        return json.loads(body)

    def wait(self, document_id: str, timeout: float) -> str:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            status, body, _ = self.request("GET", f"/api/v1/documents/{document_id}/status")
            if status == 200:
                current = json.loads(body)["status"]
                if current in TERMINAL_STATUSES:
                    return current
            time.sleep(1.5)
        return "TIMEOUT"

    def get_json(self, path: str) -> dict[str, Any] | None:
        status, body, _ = self.request("GET", path)
        return json.loads(body) if status == 200 else None

    def delete(self, document_id: str) -> None:
        self.request("DELETE", f"/api/v1/documents/{document_id}")


def evaluate_document(client: ApiClient, path: Path, truth: Truth, timeout: float, keep: bool) -> DocumentResult:
    result = DocumentResult(path, truth)
    started = time.monotonic()
    document_id: str | None = None

    try:
        document_id = client.upload(path)["id"]
        result.status = client.wait(document_id, timeout)
        result.elapsed_seconds = time.monotonic() - started

        if result.status == "COMPLETED":
            document = client.get_json(f"/api/v1/documents/{document_id}/result") or {}
            result.detected_type = document.get("classification", {}).get("detectedType", UNKNOWN_TYPE)
            result.fields = document.get("extraction", {}).get("fields", {})

            text = client.get_json(f"/api/v1/documents/{document_id}/text") or {}
            result.text_chars = sum(len(page.get("text", "").strip()) for page in text.get("pages", []))
    except (RuntimeError, OSError, KeyError, json.JSONDecodeError) as error:
        result.status = "UPLOAD_ERROR"
        result.error = type(error).__name__
    finally:
        if document_id and not keep:
            client.delete(document_id)

    return result


# ------------------------------------------------------------------------------------------ relatório

def pct(value: float | None) -> str:
    return "—" if value is None else f"{value * 100:.1f}%"


def build_report(results: list[DocumentResult], skipped: dict[str, int], args_summary: dict[str, Any],
                 overrides: dict[str, list[str]] | None, include_details: bool, include_values: bool,
                 minimum_chars: int) -> dict[str, Any]:
    report = aggregate(results, overrides)
    report["classification"] = classification_report(results)
    report["textYield"] = text_yield(results, minimum_chars)
    report["latency"] = latency_report(results)
    report["documents"] = {
        "evaluated": len(results),
        "completed": sum(item.status == "COMPLETED" for item in results),
        "notCompleted": {status: sum(item.status == status for item in results)
                         for status in sorted({item.status for item in results if item.status != "COMPLETED"})},
        **skipped,
    }
    report["indicators"] = indicators(report)
    report["schemaVersion"] = 1
    report["generatedAt"] = datetime.now(timezone.utc).isoformat(timespec="seconds")
    report["run"] = args_summary

    problems = []
    for index, result in enumerate(results, start=1):
        misses = [score for score in score_document(result) if score.outcome not in {"TP", "TN"}]
        if not misses and result.status == "COMPLETED" and result.detected_type == result.truth.document_type:
            continue

        # Sem --details o relatório não identifica o documento: nome de arquivo de documento real já é dado pessoal.
        label = str(result.file) if include_details else f"doc-{index:03d}-{hashlib.sha256(result.file.name.encode()).hexdigest()[:6]}"
        entry: dict[str, Any] = {
            "document": label, "type": result.truth.document_type, "quality": result.truth.quality,
            "status": result.status, "detectedType": result.detected_type,
            "fieldProblems": [
                {"field": score.path, "outcome": score.outcome, "reason": score.detail,
                 **({"expected": score.expected, "got": score.got} if include_values else {})}
                for score in misses
            ],
        }
        if result.error:
            entry["error"] = result.error
        problems.append(entry)

    report["problems"] = problems
    return report


def render_markdown(report: dict[str, Any]) -> str:
    lines = ["# Relatório de avaliação", "", f"Gerado em {report['generatedAt']}.", ""]
    documents = report["documents"]
    lines += [
        f"Documentos avaliados: **{documents['evaluated']}** ({documents['completed']} concluídos"
        + (f", não concluídos: {documents['notCompleted']}" if documents["notCompleted"] else "") + ")."
        + f" Sem anotação: {documents.get('unannotated', 0)}. Anotações sem documento: {documents.get('orphanTruth', 0)}.",
        "", "## Indicadores da PoC (PRD §3)", "", "| Indicador | Limiar | Medido | Resultado |", "|---|---:|---:|---|",
    ]
    for item in report["indicators"]:
        verdict = "sem dados" if item["passed"] is None else ("atingido" if item["passed"] else "**não atingido**")
        lines.append(f"| {item['description']} | {pct(item['threshold'])} | {pct(item['value'])} | {verdict} |")

    classification = report["classification"]
    lines += ["", "## Classificação", "",
              f"Acerto geral {pct(classification['accuracy'])}; nos {classification['prioritizedDocuments']} documentos de "
              f"tipos priorizados {pct(classification['prioritizedAccuracy'])}.", "",
              "| Tipo real \\ previsto | " + " | ".join(sorted({p for row in classification['confusion'].values() for p in row})) + " |"]
    predicted = sorted({p for row in classification["confusion"].values() for p in row})
    lines.append("|---|" + "---:|" * len(predicted))
    for truth, row in classification["confusion"].items():
        lines.append(f"| {truth} | " + " | ".join(str(row.get(p, 0)) for p in predicted) + " |")

    lines += ["", "## Por tipo e campo", ""]
    for name, entry in report["types"].items():
        micro = entry["micro"]
        lines += [
            f"### {name}", "",
            f"{entry['documents']} documento(s); classificação {pct(entry['classification']['accuracy'])}; "
            f"campos: P {pct(micro['precision'])} · R {pct(micro['recall'])} · F1 {pct(micro['f1'])}; "
            f"críticos: exact match {pct(entry['criticalFields']['exactMatch'])}.", "",
            "| Campo | TP | FP | FN | TN | Precision | Recall | F1 | Exatidão do DV |", "|---|---:|---:|---:|---:|---:|---:|---:|---:|",
        ]
        for field_name, counts in entry["fields"].items():
            dv = counts.get("checkDigit")
            lines.append(
                f"| {field_name} | {counts['truePositives']} | {counts['falsePositives']} | {counts['falseNegatives']} | "
                f"{counts['trueNegatives']} | {pct(counts['precision'])} | {pct(counts['recall'])} | {pct(counts['f1'])} | "
                f"{pct(dv['accuracy']) if dv else '—'} |"
            )
        lines.append("")

    if len(report["quality"]) > len(report["types"]):
        lines += ["## Por tipo e qualidade", "", "| Tipo · qualidade | Docs | Classificação | Precision | Recall | F1 |", "|---|---:|---:|---:|---:|---:|"]
        for name, entry in report["quality"].items():
            micro = entry["micro"]
            lines.append(f"| {name.replace('|', ' · ')} | {entry['documents']} | {pct(entry['classification']['accuracy'])} | "
                         f"{pct(micro['precision'])} | {pct(micro['recall'])} | {pct(micro['f1'])} |")
        lines.append("")

    if report["latency"]:
        lines += ["## Latência de ponta a ponta", "", "| Tipo | Docs | Mediana | Máxima |", "|---|---:|---:|---:|"]
        for name, entry in report["latency"].items():
            lines.append(f"| {name} | {entry['documents']} | {entry['medianSeconds']} s | {entry['maxSeconds']} s |")
        lines.append("")

    if report["problems"]:
        lines += ["## Documentos com problema", ""]
        for problem in report["problems"]:
            lines.append(f"- **{problem['document']}** ({problem['type']}, {problem['quality']}): {problem['status']}"
                         f", classificado como {problem['detectedType']}")
            for item in problem["fieldProblems"]:
                extra = f" — esperado `{item['expected']}`, veio `{item.get('got')}`" if "expected" in item else ""
                lines.append(f"  - `{item['field']}`: {item['outcome']}, {item['reason']}{extra}")
        lines.append("")

    return "\n".join(lines)


def print_summary(report: dict[str, Any]) -> None:
    documents = report["documents"]
    print(f"\nDocumentos avaliados: {documents['evaluated']} (concluídos: {documents['completed']})")
    print(f"{'Tipo':<22}{'Docs':>5}{'Classif.':>10}{'Precision':>11}{'Recall':>9}{'F1':>8}{'Críticos':>10}")
    for name, entry in report["types"].items():
        micro = entry["micro"]
        print(f"{name:<22}{entry['documents']:>5}{pct(entry['classification']['accuracy']):>10}{pct(micro['precision']):>11}"
              f"{pct(micro['recall']):>9}{pct(micro['f1']):>8}{pct(entry['criticalFields']['exactMatch']):>10}")

    print("\nIndicadores da PoC (PRD §3):")
    for item in report["indicators"]:
        verdict = "sem dados" if item["passed"] is None else ("ATINGIDO" if item["passed"] else "NÃO ATINGIDO")
        print(f"  {item['description']:<52} {pct(item['value']):>7} (limiar {pct(item['threshold'])}) {verdict}")


# ------------------------------------------------------------------------------------------ programa

def parse_overrides(values: list[str]) -> dict[str, list[str]]:
    overrides: dict[str, list[str]] = {}
    for value in values:
        name, _, fields = value.partition("=")
        if not name or not fields:
            raise SystemExit(f"--critical espera TIPO=campo1,campo2, recebeu '{value}'")
        overrides[name] = [item.strip() for item in fields.split(",") if item.strip()]
    return overrides


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Avalia o DocReader contra documentos anotados", formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dataset", required=True, type=Path, help="Pasta com os documentos e os ground truth (fora do repositório)")
    parser.add_argument("--api", default="http://localhost:8080", help="Endereço da API")
    parser.add_argument("--truth-suffix", default=".truth.json", help="Sufixo do ground truth: cnh.png -> cnh.truth.json")
    parser.add_argument("--out", type=Path, default=Path("evaluation-report"), help="Pasta do relatório (report.json e report.md)")
    parser.add_argument("--details", action="store_true", help="Identifica os documentos pelo caminho no relatório (dado pessoal em documento real)")
    parser.add_argument("--include-values", action="store_true", help="Grava o valor esperado e o devolvido de cada campo errado (implica --details)")
    parser.add_argument("--keep", action="store_true", help="Não apaga da API os documentos enviados")
    parser.add_argument("--timeout", type=float, default=600, help="Segundos de espera por documento")
    parser.add_argument("--limit", type=int, default=0, help="Avalia só os N primeiros documentos")
    parser.add_argument("--min-text-chars", type=int, default=20, help="Caracteres mínimos para o texto contar como utilizável")
    parser.add_argument("--critical", action="append", default=[], metavar="TIPO=campo,campo", help="Sobrescreve os campos críticos de um tipo (padrão: required do schema)")
    parser.add_argument("--baseline", type=Path, help="report.json anterior, para detectar regressão")
    parser.add_argument("--max-drop", type=float, default=0.02, help="Queda de F1 aceita contra o baseline")
    parser.add_argument("--fail-on-indicators", action="store_true", help="Sai com código 1 se algum indicador da PoC não for atingido")
    parser.add_argument("--allow-inside-repo", action="store_true", help="Permite uma pasta de documentos dentro do repositório")
    args = parser.parse_args(argv)

    if not args.dataset.is_dir():
        print(f"{args.dataset} não é uma pasta", file=sys.stderr)
        return 2

    check_dataset_location(args.dataset, args.allow_inside_repo)
    include_details = args.details or args.include_values
    overrides = parse_overrides(args.critical)

    pairs, unannotated, orphans = discover(args.dataset, args.truth_suffix)
    if args.limit:
        pairs = pairs[: args.limit]
    if not pairs:
        print(f"Nenhum documento anotado em {args.dataset} (procurei <arquivo>{args.truth_suffix}).", file=sys.stderr)
        return 2

    entries: list[tuple[Path, Truth]] = []
    for document, truth_path in pairs:
        try:
            entries.append((document, load_truth(truth_path)))
        except TruthError as error:
            print(f"{truth_path.name}: {error}", file=sys.stderr)
            return 2

    client = ApiClient(args.api)
    try:
        status, _, _ = client.request("GET", "/health/ready")
    except OSError as error:
        print(f"Não consegui falar com a API em {args.api} ({type(error).__name__}). Suba o sistema com docker compose up -d.", file=sys.stderr)
        return 2

    if status != 200:
        print(f"A API em {args.api} não está pronta (HTTP {status}). Suba o sistema com docker compose up -d.", file=sys.stderr)
        return 2

    print(f"Avaliando {len(entries)} documento(s) em {args.api}", flush=True)
    results: list[DocumentResult] = []
    for index, (document, truth) in enumerate(entries, start=1):
        result = evaluate_document(client, document, truth, args.timeout, args.keep)
        results.append(result)
        print(f"  [{index}/{len(entries)}] {truth.document_type:<20} {result.status:<10} {result.elapsed_seconds:5.1f}s", flush=True)

    report = build_report(
        results,
        {"unannotated": len(unannotated), "orphanTruth": len(orphans)},
        {"dataset": args.dataset.name, "api": args.api, "truthSuffix": args.truth_suffix, "details": include_details,
         "includeValues": args.include_values},
        overrides, include_details, args.include_values, args.min_text_chars,
    )

    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "report.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    (args.out / "report.md").write_text(render_markdown(report), encoding="utf-8")
    print_summary(report)
    print(f"\nRelatório em {args.out / 'report.json'} e {args.out / 'report.md'}")

    exit_code = 0
    if args.baseline:
        regressions = compare_with_baseline(report, json.loads(args.baseline.read_text(encoding="utf-8-sig")), args.max_drop)
        if regressions:
            print("\nRegressões contra o baseline:")
            for line in regressions:
                print(f"  - {line}")
            exit_code = 3
        else:
            print("\nSem regressão contra o baseline.")

    if args.fail_on_indicators and any(item["passed"] is False for item in report["indicators"]):
        exit_code = exit_code or 1

    return exit_code


if __name__ == "__main__":
    sys.exit(main())
