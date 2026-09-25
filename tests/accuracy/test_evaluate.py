"""Testes do avaliador (scripts/evaluate.py). Só biblioteca padrão e pytest; não precisam do sistema no ar."""

from __future__ import annotations

import codecs
import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))

import evaluate as ev  # noqa: E402


def got(status: str, normalized: str | None = None, raw: str | None = None, messages: list[str] | None = None) -> dict:
    return {"validationStatus": status, "normalized": normalized, "raw": raw, "validationMessages": messages or []}


def expected(value: str | None = None, status: str | None = None, **extra) -> ev.ExpectedField:
    return ev.parse_field("f", {"value": value, "status": status, **extra} if status else value)


# ------------------------------------------------------------------ ground truth

def test_campo_pode_ser_null_texto_ou_objeto():
    assert ev.parse_field("a", None) == ev.ExpectedField(None, "NOT_FOUND")
    assert ev.parse_field("a", "X") == ev.ExpectedField("X", "VALID")
    assert ev.parse_field("a", {"value": "X"}) == ev.ExpectedField("X", "VALID")


def test_normalized_e_alias_de_value_para_os_expected_das_amostras():
    field = ev.parse_field("a", {"normalized": "X", "status": "UNCERTAIN"})

    assert field == ev.ExpectedField("X", "UNCERTAIN")


def test_invalid_descreve_o_valor_lido_em_raw():
    field = ev.parse_field("cpf", {"status": "INVALID", "raw": "111.444.777-99"})

    assert field.raw == "111.444.777-99" and field.value is None


@pytest.mark.parametrize("spec", [
    {"status": "VALID"},
    {"value": "X", "status": "NOT_FOUND"},
    {"value": "X", "status": "INVALID"},
    {"value": "X", "status": "TALVEZ"},
    {"value": "X", "messages": "DOCUMENT_EXPIRED"},
    42,
])
def test_anotacao_fora_do_formato_e_rejeitada(spec):
    with pytest.raises(ev.TruthError):
        ev.parse_field("f", spec)


def test_document_type_e_obrigatorio():
    with pytest.raises(ev.TruthError):
        ev.parse_truth({"fields": {}})


def test_valores_padrao_da_anotacao():
    truth = ev.parse_truth({"documentType": "BR_CNH", "fields": {"name": "MARIA"}})

    assert truth.quality == "unspecified" and truth.legible is True


def test_caminho_de_socio_e_agregado_sem_indice():
    assert ev.generic_path("partners[3].cpf") == "partners[].cpf"
    assert ev.generic_path("cpf") == "cpf"


# ------------------------------------------------------------------ pontuação de um campo

def test_valor_igual_e_verdadeiro_positivo():
    score = ev.score_field("BR_CNH", "name", expected("MARIA"), got("VALID", "MARIA"))

    assert score.outcome == "TP" and score.counts.tp == 1


def test_valor_diferente_e_falso_positivo_e_falso_negativo():
    score = ev.score_field("BR_CNH", "name", expected("MARIA"), got("VALID", "MARIO"))

    assert score.outcome == "FP+FN" and (score.counts.fp, score.counts.fn) == (1, 1)


def test_campo_nao_encontrado_e_falso_negativo():
    assert ev.score_field("BR_CNH", "name", expected("MARIA"), got("NOT_FOUND")).outcome == "FN"
    assert ev.score_field("BR_CNH", "name", expected("MARIA"), None).outcome == "FN"


def test_campo_ausente_no_documento_e_verdadeiro_negativo_ou_falso_positivo():
    absent = ev.parse_field("tradeName", None)

    assert ev.score_field("BR_CNPJ_CARD", "tradeName", absent, got("NOT_FOUND")).outcome == "TN"
    assert ev.score_field("BR_CNPJ_CARD", "tradeName", absent, got("VALID", "X")).outcome == "FP"


def test_defeito_esperado_e_reconhecido_quando_o_sistema_devolve_invalid():
    truth = ev.parse_field("cpf", {"status": "INVALID", "raw": "111.444.777-99"})

    assert ev.score_field("BR_CNH", "cpf", truth, got("INVALID", None, "111.444.777-99")).outcome == "TP"
    assert ev.score_field("BR_CNH", "cpf", truth, got("INVALID", None, "111.444.777-00")).outcome == "FP+FN"
    assert ev.score_field("BR_CNH", "cpf", truth, got("VALID", "11144477799")).outcome == "FP+FN"
    assert ev.score_field("BR_CNH", "cpf", truth, got("NOT_FOUND")).outcome == "FN"


def test_valido_reprovado_pelo_sistema_e_falso_negativo_porque_nao_ha_valor():
    score = ev.score_field("BR_CNH", "cpf", expected("11144477735"), got("INVALID", None, "111.444.777-3S"))

    assert score.outcome == "FN"


def test_codigo_de_aviso_esperado_que_nao_veio_derruba_o_acerto():
    truth = ev.parse_field("expirationDate", {"value": "2023-07-01", "messages": ["DOCUMENT_EXPIRED"]})

    assert ev.score_field("BR_CNH", "expirationDate", truth, got("VALID", "2023-07-01", messages=["DOCUMENT_EXPIRED"])).outcome == "TP"
    assert ev.score_field("BR_CNH", "expirationDate", truth, got("VALID", "2023-07-01", messages=["DATE_VALID"])).outcome == "FN"


# ------------------------------------------------------------------ dígito verificador

def test_veredito_do_dv_concorda_quando_valido_e_valido_ou_invalido_e_invalido():
    ok = ev.score_field("BR_CNH", "cpf", expected("11144477735"), got("VALID", "11144477735"))
    bad = ev.parse_field("cpf", {"status": "INVALID", "raw": "X"})
    flagged = ev.score_field("BR_CNH", "cpf", bad, got("INVALID", None, "X"))

    assert ok.counts.dv_correct == 1 and flagged.counts.dv_correct == 1


def test_veredito_errado_e_contado_e_a_falta_de_veredito_tambem():
    wrong = ev.score_field("BR_CNH", "cpf", expected("11144477735"), got("INVALID", None, "1"))
    none = ev.score_field("BR_CNH", "cpf", expected("11144477735"), got("NOT_FOUND"))

    assert wrong.counts.dv_wrong == 1 and none.counts.dv_no_verdict == 1


def test_campo_sem_dv_nao_entra_na_exatidao_do_dv():
    score = ev.score_field("BR_CNH", "name", expected("MARIA"), got("VALID", "MARIA"))

    assert (score.counts.dv_correct, score.counts.dv_wrong, score.counts.dv_no_verdict) == (0, 0, 0)


def test_dv_de_socio_vale_para_qualquer_indice():
    score = ev.score_field("BR_SOCIAL_CONTRACT", "partners[2].cpf", expected("52998224725"), got("VALID", "52998224725"))

    assert score.counts.dv_correct == 1


def test_todo_campo_dv_declarado_existe_no_schema_do_tipo():
    for document_type, names in ev.DV_FIELDS.items():
        schema = json.loads((ev.SCHEMAS_DIR / f"{document_type}.v1.json").read_text(encoding="utf-8"))
        for name in names:
            assert any(ev.generic_path(key) == name for key in schema["properties"]) or name.startswith("partners[]"), (document_type, name)


# ------------------------------------------------------------------ métricas

def test_precision_recall_e_f1():
    counts = ev.Counts(tp=8, fp=2, fn=4)

    assert counts.precision == 0.8
    assert round(counts.recall, 4) == 0.6667
    assert round(counts.f1, 4) == 0.7273


def test_metricas_sem_denominador_sao_none_e_nao_zero():
    empty = ev.Counts(tn=3)

    assert empty.precision is None and empty.recall is None and empty.f1 is None and empty.dv_accuracy is None


def _result(doc_type="BR_CNH", detected="BR_CNH", status="COMPLETED", fields=None, truth_fields=None, quality="clean", text=100, seconds=5.0):
    truth = ev.parse_truth({"documentType": doc_type, "quality": quality, "fields": truth_fields or {}})
    return ev.DocumentResult(Path(f"{doc_type}-{quality}.png"), truth, status, detected, fields or {}, text, seconds)


def test_agregacao_soma_por_tipo_e_por_campo():
    results = [
        _result(truth_fields={"name": "A", "cpf": "1"}, fields={"name": got("VALID", "A"), "cpf": got("VALID", "1")}),
        _result(truth_fields={"name": "B", "cpf": "2"}, fields={"name": got("VALID", "X"), "cpf": got("NOT_FOUND")}),
    ]

    report = ev.aggregate(results, {"BR_CNH": ["name"]})
    entry = report["types"]["BR_CNH"]

    assert entry["documents"] == 2
    assert entry["fields"]["name"]["truePositives"] == 1 and entry["fields"]["name"]["falsePositives"] == 1
    assert entry["fields"]["cpf"]["falseNegatives"] == 1
    assert entry["micro"]["truePositives"] == 2
    # críticos: só "name" (sobrescrito): 1 acerto e 1 erro
    assert entry["criticalFields"]["truePositives"] == 1 and entry["criticalFields"]["falseNegatives"] == 1


def test_documento_que_falhou_conta_os_campos_presentes_como_falso_negativo_e_ignora_os_ausentes():
    failed = _result(status="FAILED", detected="NONE", truth_fields={"name": "A", "tradeName": None})

    scores = ev.score_document(failed)

    assert [(score.path, score.outcome) for score in scores] == [("name", "FN")]


def test_agregacao_separa_por_qualidade():
    results = [
        _result(quality="clean", truth_fields={"name": "A"}, fields={"name": got("VALID", "A")}),
        _result(quality="photo", truth_fields={"name": "A"}, fields={"name": got("VALID", "Z")}),
    ]

    quality = ev.aggregate(results)["quality"]

    assert quality["BR_CNH|clean"]["micro"]["f1"] == 1.0
    assert quality["BR_CNH|photo"]["micro"]["f1"] == 0.0


def test_classificacao_com_matriz_de_confusao():
    results = [
        _result("BR_CNH", "BR_CNH"), _result("BR_CNH", "BR_CIN"), _result("BR_CIN", "BR_CIN"),
        _result("UNKNOWN", "UNKNOWN"), _result("BR_CNH", "NONE", status="FAILED"),
    ]

    report = ev.classification_report(results)

    assert report["accuracy"] == 0.6
    assert report["prioritizedAccuracy"] == 0.5  # 2 de 4 entre os tipos priorizados; UNKNOWN fica de fora
    assert report["confusion"]["BR_CNH"] == {"BR_CIN": 1, "BR_CNH": 1, "FAILED": 1}


def test_rendimento_de_texto_so_conta_documento_legivel():
    truth_ilegivel = ev.parse_truth({"documentType": "UNKNOWN", "legible": False})
    results = [
        _result(text=100), _result(text=5), _result(status="FAILED", text=0),
        ev.DocumentResult(Path("x.png"), truth_ilegivel, "COMPLETED", "UNKNOWN", {}, 0),
    ]

    report = ev.text_yield(results, minimum_chars=20)

    assert report["legibleDocuments"] == 3 and report["usable"] == 1


def test_indicadores_comparam_com_os_limiares_do_prd():
    results = [_result(truth_fields={"name": "A"}, fields={"name": got("VALID", "A")}) for _ in range(9)]
    results.append(_result(truth_fields={"name": "A"}, fields={"name": got("VALID", "Z")}))
    report = ev.aggregate(results, {"BR_CNH": ["name"]})
    report["classification"] = ev.classification_report(results)
    report["textYield"] = ev.text_yield(results, 20)

    by_id = {item["id"]: item for item in ev.indicators(report)}

    assert by_id["classification"]["passed"] is True
    assert by_id["textYield"]["passed"] is True
    assert by_id["criticalExactMatch"]["value"] == 0.9 and by_id["criticalExactMatch"]["passed"] is True


def test_markdown_por_qualidade_nao_deixa_barra_vertical_dentro_de_celula():
    results = [_result(quality="photo", truth_fields={"name": "A"}, fields={"name": got("VALID", "A")}),
               _result(quality="clean", truth_fields={"name": "A"}, fields={"name": got("VALID", "A")})]
    report = ev.build_report(results, {}, {}, None, include_details=False, include_values=False, minimum_chars=20)

    markdown = ev.render_markdown(report)

    assert "| BR_CNH · photo |" in markdown and "BR_CNH|photo" not in markdown


def test_indicador_sem_dados_nao_reprova():
    report = ev.aggregate([])
    report["classification"] = ev.classification_report([])
    report["textYield"] = ev.text_yield([], 20)

    assert all(item["passed"] is None for item in ev.indicators(report))


def test_campos_criticos_vem_do_required_do_schema():
    assert ev.critical_fields("BR_CNH") == ["name", "cpf", "birthDate", "registrationNumber"]
    assert ev.critical_fields("TIPO_INEXISTENTE") == []
    assert ev.critical_fields("BR_CNH", {"BR_CNH": ["cpf"]}) == ["cpf"]


# ------------------------------------------------------------------ regressão

def test_baseline_acusa_queda_de_f1_acima_do_limite():
    before = ev.aggregate([_result(truth_fields={"name": "A"}, fields={"name": got("VALID", "A")})])
    after = ev.aggregate([_result(truth_fields={"name": "A"}, fields={"name": got("VALID", "Z")})])

    assert ev.compare_with_baseline(after, before, 0.02) == ["BR_CNH.name: F1 1.000 -> 0.000"]
    assert ev.compare_with_baseline(before, after, 0.02) == []


def test_baseline_acusa_tipo_que_sumiu():
    before = ev.aggregate([_result(truth_fields={"name": "A"}, fields={"name": got("VALID", "A")})])

    assert ev.compare_with_baseline(ev.aggregate([]), before, 0.02) == ["BR_CNH: sem documentos nesta rodada"]


# ------------------------------------------------------------------ dados reais

def test_pasta_dentro_do_repositorio_e_recusada_com_codigo_2_salvo_as_amostras_sinteticas(tmp_path, capsys):
    inside = ev.REPO_ROOT / "docs"

    with pytest.raises(SystemExit) as refusal:
        ev.check_dataset_location(inside, allow_inside_repo=False)
    assert refusal.value.code == 2
    assert "dentro do repositório" in capsys.readouterr().err
    ev.check_dataset_location(inside, allow_inside_repo=True)
    ev.check_dataset_location(ev.SYNTHETIC_ROOT / "documents", allow_inside_repo=False)
    ev.check_dataset_location(tmp_path, allow_inside_repo=False)


def test_relatorio_por_padrao_nao_traz_nome_de_arquivo_nem_valor():
    result = _result(truth_fields={"name": "MARIA"}, fields={"name": got("VALID", "MARIO")})
    result.file = Path("/dados/maria-silva-cnh.jpg")

    report = ev.build_report([result], {}, {}, None, include_details=False, include_values=False, minimum_chars=20)
    text = json.dumps(report, ensure_ascii=False)

    assert "maria-silva" not in text and "MARIA" not in text and "MARIO" not in text
    assert report["problems"][0]["fieldProblems"][0]["outcome"] == "FP+FN"


def test_relatorio_com_include_values_traz_o_esperado_e_o_devolvido():
    result = _result(truth_fields={"name": "MARIA"}, fields={"name": got("VALID", "MARIO")})

    report = ev.build_report([result], {}, {}, None, include_details=True, include_values=True, minimum_chars=20)

    problem = report["problems"][0]["fieldProblems"][0]
    assert problem["expected"] == "MARIA" and problem["got"] == "MARIO"


def test_descoberta_pareia_documento_e_anotacao_e_lista_o_resto(tmp_path):
    (tmp_path / "a.png").write_bytes(b"x")
    (tmp_path / "a.truth.json").write_text("{}")
    (tmp_path / "b.pdf").write_bytes(b"x")
    (tmp_path / "sub").mkdir()
    (tmp_path / "sub" / "c.jpg").write_bytes(b"x")
    (tmp_path / "sub" / "c.truth.json").write_text("{}")
    (tmp_path / "orfa.truth.json").write_text("{}")
    (tmp_path / "leiame.txt").write_text("x")

    pairs, unannotated, orphans = ev.discover(tmp_path, ".truth.json")

    assert [doc.name for doc, _ in pairs] == ["a.png", "c.jpg"]
    assert [path.name for path in unannotated] == ["b.pdf"]
    assert [path.name for path in orphans] == ["orfa.truth.json"]


# ------------------------------------------------------------------ ponta a ponta com API falsa

class FakeApi(BaseHTTPRequestHandler):
    """Implementa só o que o avaliador usa. Devolve, para cada documento, o que `RESULTS` diz pelo nome."""

    RESULTS: dict[str, dict] = {}
    uploaded: dict[str, str] = {}
    deleted: list[str] = []

    def log_message(self, *args) -> None:  # silencia o servidor
        pass

    def _json(self, status: int, payload: dict | None = None) -> None:
        body = json.dumps(payload or {}).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:
        if self.path == "/health/ready":
            return self._json(200)
        parts = self.path.strip("/").split("/")
        name = FakeApi.uploaded.get(parts[3]) if len(parts) > 3 else None
        canned = FakeApi.RESULTS.get(name or "", {})
        if self.path.endswith("/status"):
            return self._json(200, {"status": canned.get("status", "COMPLETED")})
        if self.path.endswith("/result"):
            return self._json(200, {"classification": {"detectedType": canned["type"]}, "extraction": {"fields": canned["fields"]}})
        if self.path.endswith("/text"):
            return self._json(200, {"pages": [{"page": 1, "text": canned.get("text", "x" * 200)}]})
        return self._json(404)

    def do_POST(self) -> None:
        length = int(self.headers["Content-Length"])
        body = self.rfile.read(length)
        marker = b'filename="'
        name = body[body.index(marker) + len(marker):].split(b'"', 1)[0].decode()
        identifier = f"id-{len(FakeApi.uploaded)}"
        FakeApi.uploaded[identifier] = name
        self._json(202, {"id": identifier})

    def do_DELETE(self) -> None:
        FakeApi.deleted.append(self.path.rsplit("/", 1)[-1])
        self._json(204)


@pytest.fixture()
def fake_api():
    FakeApi.uploaded, FakeApi.deleted, FakeApi.RESULTS = {}, [], {}
    server = HTTPServer(("127.0.0.1", 0), FakeApi)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    yield f"http://127.0.0.1:{server.server_port}"
    server.shutdown()


def _dataset(tmp_path: Path) -> Path:
    data = tmp_path / "dados"
    data.mkdir()
    (data / "cnh1.png").write_bytes(b"\x89PNG")
    (data / "cnh1.truth.json").write_text(json.dumps({
        "documentType": "BR_CNH", "quality": "clean",
        "fields": {"name": "MARIA", "cpf": "11144477735", "registrationNumber": None},
    }))
    (data / "cnh2.png").write_bytes(b"\x89PNG")
    (data / "cnh2.truth.json").write_text(json.dumps({
        "documentType": "BR_CNH", "quality": "photo",
        "fields": {"name": "JOAO", "cpf": {"status": "INVALID", "raw": "111.444.777-99"}},
    }))
    return data


def test_execucao_completa_gera_relatorios_apaga_o_que_enviou_e_nao_vaza_valores(fake_api, tmp_path, capsys):
    FakeApi.RESULTS = {
        "cnh1.png": {"type": "BR_CNH", "fields": {"name": got("VALID", "MARIA"), "cpf": got("VALID", "11144477735"),
                                                  "registrationNumber": got("NOT_FOUND")}},
        "cnh2.png": {"type": "BR_CNH", "fields": {"name": got("VALID", "JOAO"), "cpf": got("INVALID", None, "111.444.777-99")}},
    }
    out = tmp_path / "relatorio"

    code = ev.main(["--dataset", str(_dataset(tmp_path)), "--api", fake_api, "--out", str(out), "--timeout", "10"])

    assert code == 0
    report = json.loads((out / "report.json").read_text(encoding="utf-8"))
    assert report["documents"]["completed"] == 2
    assert report["types"]["BR_CNH"]["micro"]["f1"] == 1.0
    assert report["types"]["BR_CNH"]["fields"]["cpf"]["checkDigit"]["accuracy"] == 1.0
    assert report["types"]["BR_CNH"]["fields"]["registrationNumber"]["trueNegatives"] == 1
    assert {item["id"]: item["passed"] for item in report["indicators"]}["classification"] is True
    assert sorted(FakeApi.deleted) == ["id-0", "id-1"]
    assert (out / "report.md").read_text(encoding="utf-8").startswith("# Relatório de avaliação")
    assert "MARIA" not in (out / "report.json").read_text(encoding="utf-8")


def test_keep_preserva_os_documentos_na_api(fake_api, tmp_path):
    FakeApi.RESULTS = {"cnh1.png": {"type": "BR_CNH", "fields": {}}, "cnh2.png": {"type": "BR_CNH", "fields": {}}}

    ev.main(["--dataset", str(_dataset(tmp_path)), "--api", fake_api, "--out", str(tmp_path / "r"), "--keep", "--timeout", "10"])

    assert FakeApi.deleted == []


def test_documento_que_falha_derruba_o_recall_e_a_saida_respeita_fail_on_indicators(fake_api, tmp_path):
    FakeApi.RESULTS = {
        "cnh1.png": {"status": "FAILED", "type": "UNKNOWN", "fields": {}},
        "cnh2.png": {"type": "BR_CNH", "fields": {"name": got("VALID", "JOAO"), "cpf": got("INVALID", None, "111.444.777-99")}},
    }
    out = tmp_path / "r"

    code = ev.main(["--dataset", str(_dataset(tmp_path)), "--api", fake_api, "--out", str(out), "--fail-on-indicators", "--timeout", "10"])

    assert code == 1
    report = json.loads((out / "report.json").read_text(encoding="utf-8"))
    assert report["documents"]["notCompleted"] == {"FAILED": 1}
    assert report["types"]["BR_CNH"]["fields"]["name"]["falseNegatives"] == 1


def test_baseline_com_regressao_sai_com_codigo_3(fake_api, tmp_path):
    good = {
        "cnh1.png": {"type": "BR_CNH", "fields": {"name": got("VALID", "MARIA"), "cpf": got("VALID", "11144477735"), "registrationNumber": got("NOT_FOUND")}},
        "cnh2.png": {"type": "BR_CNH", "fields": {"name": got("VALID", "JOAO"), "cpf": got("INVALID", None, "111.444.777-99")}},
    }
    data = _dataset(tmp_path)
    FakeApi.RESULTS = good
    ev.main(["--dataset", str(data), "--api", fake_api, "--out", str(tmp_path / "base"), "--timeout", "10"])

    FakeApi.uploaded = {}
    FakeApi.RESULTS = {**good, "cnh1.png": {"type": "BR_CNH", "fields": {"name": got("VALID", "ERRADO"), "cpf": got("NOT_FOUND"), "registrationNumber": got("NOT_FOUND")}}}
    code = ev.main(["--dataset", str(data), "--api", fake_api, "--out", str(tmp_path / "now"),
                    "--baseline", str(tmp_path / "base" / "report.json"), "--timeout", "10"])

    assert code == 3


def test_anotacao_com_bom_utf8_do_powershell_e_lida(tmp_path):
    path = tmp_path / "a.truth.json"
    path.write_bytes(codecs.BOM_UTF8 + json.dumps({"documentType": "BR_CNH", "fields": {"name": "MARIA"}}).encode("utf-8"))

    assert ev.load_truth(path).fields["name"].value == "MARIA"


def test_pasta_sem_anotacao_e_erro_de_uso(tmp_path, capsys):
    (tmp_path / "a.png").write_bytes(b"x")

    assert ev.main(["--dataset", str(tmp_path)]) == 2
    assert "Nenhum documento anotado" in capsys.readouterr().err


def test_anotacao_invalida_e_erro_de_uso(tmp_path, capsys):
    (tmp_path / "a.png").write_bytes(b"x")
    (tmp_path / "a.truth.json").write_text(json.dumps({"fields": {}}))

    assert ev.main(["--dataset", str(tmp_path)]) == 2
    assert "documentType" in capsys.readouterr().err


def test_api_fora_do_ar_e_erro_de_uso(tmp_path, capsys):
    data = _dataset(tmp_path)

    assert ev.main(["--dataset", str(data), "--api", "http://127.0.0.1:9"]) == 2
    assert "Não consegui falar com a API" in capsys.readouterr().err
