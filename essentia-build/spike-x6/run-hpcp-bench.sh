#!/usr/bin/env bash
# x7 step 0: runs essentia_standard_x7_hpcp_bench over the real rows of
# manifest-prod.tsv (production-shaped input: the hi-res rows already converted
# to 44.1 kHz) and reduces the results to hpcp-bench.txt.
#   run-hpcp-bench.sh <tools dir> [repeats=5]
#     e.g. run-hpcp-bench.sh out/tools-build-p7
# Raw lines: out/hpcp-bench.raw. Status: out/hpcp-bench.log, last line HPCP_DONE.
set -u
cd "$(dirname "$0")" || exit 1
tools="$1"; repeats="${2:-5}"
exe="$tools/essentia_standard_x7_hpcp_bench.exe"
[ -f "$exe" ] || { echo "no $exe"; exit 1; }
status=out/hpcp-bench.log
mkdir -p out
echo "START $(date) exe=$exe repeats=$repeats" > "$status"
md5sum "$exe" >> "$status" 2>&1
: > out/hpcp-bench.raw
tail -n +2 manifest-prod.tsv | while IFS=$'\t' read -r id kind path expect note; do
  [ "$kind" = real ] && [ "$expect" = decode ] || continue
  echo "row=$id" >> out/hpcp-bench.raw
  "$exe" "$path" "$repeats" >> out/hpcp-bench.raw 2>> "$status" || echo "FAILED: $id" >> "$status"
done
python -I hpcp_summary.py out/hpcp-bench.raw > hpcp-bench.txt 2>> "$status"
echo "HPCP_DONE $(date)" >> "$status"
