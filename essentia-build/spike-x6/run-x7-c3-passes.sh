#!/usr/bin/env bash
# The corpus passes for .7 change 3 (the in-RAM shared-decode spool), from one
# scheduled task:
#   run-x7-c3-passes.sh <build alias>       e.g. c3
# 1. gate outputs (profiler off, manifest.tsv), spool ON at its default cap:
#      <b>raw <b>scan <b>scanedges      vs drop 2's sraw / sscan / sscanedges
# 2. the forced over-cap fallback (x7.spoolMaxBytes: 1 - every pass decodes):
#      <b>zscanedges                     vs drop 2's sscanedges
# 3. CPU, production-shaped (manifest-prod.tsv), profiler on, two passes each,
#    spool on and spool off on the SAME binary:
#      <b>pa <b>pb   (on)    <b>na <b>nb   (x7.sharedDecode: 0)
# 4. memory: a 100 MiB cap so the corpus has short, spooled and over-cap rows:
#      <b>ca   (production-shaped, profiler on: peakws_mib + written_mib)
# Status: out/x7c3-<b>.log, last line X7C3_DONE (a completion marker only).
set -u
cd "$(dirname "$0")" || exit 1
b="$1"
status="out/x7c3-$b.log"
mkdir -p out
echo "START $(date) build=$b" > "$status"
bash run-repeat.sh "$b"                                    "${b}raw"       >> "$status" 2>&1
bash run-repeat.sh "$b.scan"                               "${b}scan"      >> "$status" 2>&1
bash run-repeat.sh "$b.scanedges"                          "${b}scanedges" >> "$status" 2>&1
bash run-repeat.sh "$b.pfe=x7-cap1-scanedges"              "${b}zscanedges" >> "$status" 2>&1
bash run-repeat.sh "$b.prof.scanedges.prod"                "${b}pa" "${b}pb" >> "$status" 2>&1
bash run-repeat.sh "$b.prof.pfe=x7-nospool-scanedges.prod" "${b}na" "${b}nb" >> "$status" 2>&1
bash run-repeat.sh "$b.prof.pfe=x7-cap100m-scanedges.prod" "${b}ca"        >> "$status" 2>&1
echo "X7C3_DONE $(date)" >> "$status"
