#!/usr/bin/env bash
# Experiment of ADR 0002 (latency follow-up): PP-OCRv6 sizes and downscaling before OCR, against the
# PP-OCRv5 mobile pipeline that ships, all with oneDNN on and the 3 GiB the service runs with.
#
# Runs inside the docreader/ocr-service image, which already carries paddlepaddle 3.2.2, paddleocr and
# the application code (the benchmark reuses app.pages._limit_side, so the downscale measured is the
# one production applies). One process per configuration, so the peak RSS is per configuration.
#
#   docker run --rm --user root --cpus=4 --memory=3g --entrypoint bash \
#     -v "$PWD/scripts:/scripts:ro" -v "$PWD/samples/synthetic/ocr:/samples:ro" \
#     -v ocr-bench-models:/models -v "$PWD/docs/bench:/out" \
#     docreader/ocr-service:stage2 /scripts/ocr_latency_experiment.sh
#
# OMP_NUM_THREADS (default 4) and OUT_SUFFIX (appended to the output name) let a second run vary the CPU
# regime without overwriting the first, e.g. docker run without --cpus and -e OUT_SUFFIX=-cpus8-omp4.
#
# Optional argument: a comma separated list of "pipeline|max_side" entries to run instead of the
# default matrix, e.g. "PP-OCRv6 small|1600,PP-OCRv6 tiny|0".
set -uo pipefail

export FLAGS_use_mkldnn=true
export PADDLE_PDX_CACHE_HOME=/models
export PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK=True
export OMP_NUM_THREADS="${OMP_NUM_THREADS:-4}"

DEFAULT_MATRIX=(
  "PP-OCRv5 (det mobile)|0"
  "PP-OCRv6 medium|0"
  "PP-OCRv5 (det mobile)|2000"
  "PP-OCRv6 medium|2000"
  "PP-OCRv6 small|0"
  "PP-OCRv6 small|2000"
  "PP-OCRv6 tiny|0"
  "PP-OCRv6 tiny|2000"
)

if [ -n "${1:-}" ]; then
  IFS=',' read -r -a MATRIX <<< "$1"
else
  MATRIX=("${DEFAULT_MATRIX[@]}")
fi

for entry in "${MATRIX[@]}"; do
  pipeline="${entry%|*}"
  max_side="${entry##*|}"
  slug="$(echo "${pipeline}" | tr 'A-Z ()' 'a-z---' | sed 's/--*/-/g; s/-$//')"
  output="/out/ocr-latency-${slug}-side${max_side}${OUT_SUFFIX:-}.json"

  echo "##### ${pipeline} | max_side=${max_side} -> ${output}"
  python /scripts/ocr_benchmark.py \
    --samples /samples --repeat 2 --only "${pipeline}" --max-side "${max_side}" --output "${output}"
  echo "exit=$?"
done
