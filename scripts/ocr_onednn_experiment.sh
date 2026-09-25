#!/usr/bin/env bash
# Experiment of ADR 0002: does an older CPU build of PaddlePaddle run PP-OCRv5 with oneDNN enabled,
# and how much faster is it than the 3.3.1 build with oneDNN off?
#
# Runs inside the docreader/ocr-bench image. Each candidate gets a venv that sees the image's
# site-packages (paddleocr, paddlex, opencv) and overrides only paddlepaddle, so trying a version
# costs one wheel instead of a full reinstall.
#
#   docker run --rm --cpus=4 --memory=6g --entrypoint bash \
#     -v "$PWD/scripts:/scripts:ro" -v "$PWD/samples/synthetic/ocr:/samples:ro" \
#     -v ocr-bench-models:/var/lib/docreader/ocr-models -v ocr-onednn-venvs:/venvs \
#     -v "$PWD/docs/bench:/out" \
#     docreader/ocr-bench /scripts/ocr_onednn_experiment.sh 3.2.2 true   # or false, as the control
set -uo pipefail

VERSION="${1:?usage: ocr_onednn_experiment.sh <paddlepaddle-version> [true|false]}"
MKLDNN="${2:-true}"
VENV="/venvs/pp${VERSION}"
SAMPLES="cpf-card-limpo.png,cpf-card-escaneado.png,pagina-tabela.png,pagina-texto-densa.png"

if [ ! -x "${VENV}/bin/python" ]; then
  python -m venv --system-site-packages "${VENV}"
  "${VENV}/bin/pip" install --no-cache-dir "paddlepaddle==${VERSION}" || exit 10
fi

"${VENV}/bin/python" -c "import paddle; print('paddlepaddle', paddle.__version__)"

export FLAGS_use_mkldnn="${MKLDNN}"
"${VENV}/bin/python" /scripts/ocr_benchmark.py \
  --samples /samples --repeat 2 --only "PP-OCRv5 (det mobile)" --samples-only "${SAMPLES}" \
  --output "/out/ocr-onednn-pp${VERSION}-mkldnn-${MKLDNN}.json"
echo "exit=$?"
