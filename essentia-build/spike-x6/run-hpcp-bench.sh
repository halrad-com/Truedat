#!/usr/bin/env bash
# x7 step 0: runs essentia_standard_x7_hpcp_bench over the real rows of
# manifest-prod.tsv (production-shaped input: the hi-res rows already converted
# to 44.1 kHz) and reduces the results to hpcp-bench.txt.
#   run-hpcp-bench.sh <tools dir> [repeats=5]
#     e.g. run-hpcp-bench.sh out/tools-build-p7
# Raw lines: out/hpcp-bench.raw. Status: out/hpcp-bench.log, last line
# HPCP_DONE rc=<n>. Exit status: nonzero when any row's bench run failed (it
# exits 1 on an HPCPFused mismatch) or the summary did not pass - the summary
# also fails a row with no fused comparison - so a caller can trust it.
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
failed=0
# process substitution, not a pipe: the loop must run in this shell for $failed to survive it
while IFS=$'\t' read -r id kind path expect note; do
  [ "$kind" = real ] && [ "$expect" = decode ] || continue
  echo "row=$id" >> out/hpcp-bench.raw
  "$exe" "$path" "$repeats" >> out/hpcp-bench.raw 2>> "$status"
  brc=$?
  if [ $brc -ne 0 ]; then
    echo "failed=$brc" >> out/hpcp-bench.raw
    echo "FAILED: $id (rc $brc)" >> "$status"
    failed=$((failed + 1))
  fi
done < <(tail -n +2 manifest-prod.tsv)
python -I hpcp_summary.py out/hpcp-bench.raw > hpcp-bench.txt 2>> "$status"
src=$?
rc=0
if [ $failed -ne 0 ] || [ $src -ne 0 ]; then rc=1; fi
echo "HPCP_DONE $(date) rc=$rc (bench failures $failed, summary rc $src)" >> "$status"
exit "$rc"
