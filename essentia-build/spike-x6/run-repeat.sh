#!/usr/bin/env bash
# Runs one extractor over every manifest row, once per label, with the plain
# 2-arg call (input output) - the same-binary repeat runs that define the
# per-field acceptance band, and the cross-build runs judged against it.
#   run-repeat.sh <extractor.exe | x5 | x6 | a> <label> [<label> ...]
#     aliases (schtasks /tr is capped at 261 characters): x5 = output-x64.5,
#     x6 = output-x64.6, a = out/build-a (the legacy-refactor build)
# Output: out/<label>/<id>.json and out/<label>/<id>.log (stdout+stderr), and
# one line per run in out/<label>/runs.tsv (id, rc, wall ms).
# Long (minutes per real track): launch through a one-shot scheduled task,
# not the agent Bash tool. Writes "REPEAT_DONE" to out/repeat-<first label>.log.
set -u
cd "$(dirname "$0")" || exit 1
exe="$1"; shift
case "$exe" in
  x5) exe=../output-x64.5/essentia_streaming_extractor_music.exe ;;
  x6) exe=../output-x64.6/essentia_streaming_extractor_music.exe ;;
  a)  exe=out/build-a/essentia_streaming_extractor_music.exe ;;
esac
status="out/repeat-$1.log"
mkdir -p out
echo "START $(date) exe=$exe labels=$*" > "$status"
md5sum "$exe" >> "$status" 2>&1
for label in "$@"; do
  mkdir -p "out/$label"
  printf 'id\trc\tms\n' > "out/$label/runs.tsv"
  tail -n +2 manifest.tsv | while IFS=$'\t' read -r id kind path expect note; do
    s=$(date +%s%N)
    "$exe" "$path" "out/$label/$id.json" > "out/$label/$id.log" 2>&1
    rc=$?
    e=$(date +%s%N)
    printf '%s\t%d\t%d\n' "$id" "$rc" $(( (e - s) / 1000000 )) >> "out/$label/runs.tsv"
  done
  echo "label $label done $(date)" >> "$status"
done
echo "REPEAT_DONE $(date)" >> "$status"
