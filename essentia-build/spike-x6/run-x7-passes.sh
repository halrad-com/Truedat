#!/usr/bin/env bash
# The corpus passes for one .7 change build, from one scheduled task:
#   run-x7-passes.sh <build alias>      e.g. c1 (run-repeat.sh aliases)
# Gate outputs, profiler OFF, judged against the drop 2 binary's sraw / sscan /
# sscanedges (band.py cross / scan, espb_compare.py gate, --band s3):
#   <b>raw        2-arg call (the full extractor)
#   <b>scan       scan-profile.yaml
#   <b>scanedges  scan-edges-profile.yaml --frames-bin
# Then the CPU measurement, profiler ON, production-shaped input
# (manifest-prod.tsv), two passes - compared with step 0's p0a / p0b:
#   <b>pa <b>pb   scan-edges profile + --frames-bin
# Status: out/x7-<b>.log, last line X7_DONE (a completion marker only: the
# gates decide success).
set -u
cd "$(dirname "$0")" || exit 1
b="$1"
status="out/x7-$b.log"
mkdir -p out
echo "START $(date) build=$b" > "$status"
bash run-repeat.sh "$b"                      "${b}raw"       >> "$status" 2>&1
bash run-repeat.sh "$b.scan"                 "${b}scan"      >> "$status" 2>&1
bash run-repeat.sh "$b.scanedges"            "${b}scanedges" >> "$status" 2>&1
bash run-repeat.sh "$b.prof.scanedges.prod"  "${b}pa" "${b}pb" >> "$status" 2>&1
echo "X7_DONE $(date)" >> "$status"
