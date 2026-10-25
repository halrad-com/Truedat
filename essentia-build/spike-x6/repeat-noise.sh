#!/usr/bin/env bash
# Repeats ONE corpus row N times per extractor, to show whether a key that
# moved between two builds also moves between runs of one build.
#   repeat-noise.sh <row id> <runs> <mode> <build alias> [<build alias> ...]
#     mode: scan | scanedges | raw     (the profile, as run-repeat.sh)
#   e.g. repeat-noise.sh r_m4a_2 3 scan s c1
# Output: out/noise-<build>-<n>/<row id>.json (+ .log), then
#   python -I noise_keys.py <row id> <labels...> prints the keys that moved.
# Status: out/noise-<row id>.log, last line NOISE_DONE.
set -u
cd "$(dirname "$0")" || exit 1
row="$1"; runs="$2"; mode="$3"; shift 3
path=$(awk -F'\t' -v r="$row" '$1 == r { print $3 }' manifest.tsv)
[ -n "$path" ] || { echo "no row $row in manifest.tsv"; exit 1; }
case "$mode" in
  scan)      extra=(scan-profile.yaml) ;;
  scanedges) extra=(scan-edges-profile.yaml) ;;
  raw)       extra=() ;;
  *) echo "mode: scan | scanedges | raw"; exit 1 ;;
esac
status="out/noise-$row.log"
mkdir -p out
echo "START $(date) row=$row runs=$runs mode=$mode builds=$*" > "$status"
for b in "$@"; do
  case "$b" in
    s) exe=out/build-s3/essentia_streaming_extractor_music.exe ;;
    *) exe=out/build-$b/essentia_streaming_extractor_music.exe ;;
  esac
  for n in $(seq 1 "$runs"); do
    d="out/noise-$b-$n"
    mkdir -p "$d"
    rm -f "$d/$row.json" "$d/$row.log"
    bin=()
    [ "$mode" = scanedges ] && bin=(--frames-bin "$d/$row.bin")
    "$exe" "$path" "$d/$row.json" "${extra[@]}" "${bin[@]}" > "$d/$row.log" 2>&1
    echo "$b run $n rc=$? $(date)" >> "$status"
  done
done
echo "NOISE_DONE $(date)" >> "$status"
