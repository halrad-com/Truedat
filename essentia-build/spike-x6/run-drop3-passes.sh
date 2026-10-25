#!/usr/bin/env bash
# Runs the drop 3 corpus passes for one stage build from one scheduled task:
#   run-drop3-passes.sh <stage>      stage = m1 | m2 (run-repeat.sh build aliases)
# Per stage, profiler OFF for the outputs the gate compares against the drop 2
# binary's (sraw / sscan / sscanedges from run-drop2-passes.sh gate), then
# profiler ON for the elapsed table and the peak-working-set figure:
#   <stage>raw         2-arg call (the full extractor; the muxer runs in every shape)
#   <stage>scan        scan-profile.yaml
#   <stage>scanedges   scan-edges-profile.yaml --frames-bin
#   <stage>sa          scan profile, profiler on
#   <stage>sea         scan + edges profile, profiler on
# Status: out/drop3-<stage>.log, last line DROP3_DONE.
set -u
cd "$(dirname "$0")" || exit 1
stage="$1"; shift
status="out/drop3-$stage.log"
mkdir -p out
echo "START $(date) stage=$stage" > "$status"
bash run-repeat.sh "$stage"           "${stage}raw"       >> "$status" 2>&1
bash run-repeat.sh "$stage.scan"      "${stage}scan"      >> "$status" 2>&1
bash run-repeat.sh "$stage.scanedges" "${stage}scanedges" >> "$status" 2>&1
bash run-repeat.sh "$stage.prof.scan"      "${stage}sa"   >> "$status" 2>&1
bash run-repeat.sh "$stage.prof.scanedges" "${stage}sea"  >> "$status" 2>&1
echo "DROP3_DONE $(date)" >> "$status"
