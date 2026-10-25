#!/usr/bin/env bash
# Runs the drop 2 corpus passes back to back from one scheduled task (schtasks
# carries one short command line and no environment):
#   run-drop2-passes.sh <stage>
#     baseline   profiler build (out/build-p), FULL extractor: p.prof pa pb
#                (two CPU samples, same binary, 2-arg call) and p.prof.edges
#                pea (edges call shape); plus x6.edges x6ea, the decoder-only
#                .6's edges-shape OUTPUT the scan profile is gated against
#                (its scan-only outputs are x6a/x6b from drop 1)
#     cuts       drop 2 build (out/build-s), profiler on: the full profile
#                (s.prof sfa sfb: the output-neutral code changes alone), each
#                cut alone (s.prof.cut=<name> sc-<name>), the scan profile
#                (s.prof.scan ssa ssb) and scan+edges (s.prof.scanedges ssea)
#     gate       drop 2 build, profiler OFF, the outputs the gate compares:
#                s.full sfull (must equal x6a), s sraw (2-arg, same), s.scan
#                sscan, s.scanedges sscanedges, s.edges sedges (must equal x6ea)
#     <other>    passed straight to run-repeat.sh: "<spec> <label> [<label>...]"
# Status: out/drop2-<stage>.log, last line DROP2_DONE.
set -u
cd "$(dirname "$0")" || exit 1
stage="$1"; shift
status="out/drop2-$stage.log"
mkdir -p out
echo "START $(date) stage=$stage" > "$status"
case "$stage" in
  baseline)
    bash run-repeat.sh p.prof pa pb        >> "$status" 2>&1
    bash run-repeat.sh p.prof.edges pea    >> "$status" 2>&1
    bash run-repeat.sh x6.edges x6ea       >> "$status" 2>&1
    ;;
  cuts)
    bash run-repeat.sh s.prof sfa sfb                        >> "$status" 2>&1
    for c in stats melbands128 bands md5; do
      bash run-repeat.sh "s.prof.cut=$c" "sc-$c"             >> "$status" 2>&1
    done
    bash run-repeat.sh s.prof.scan ssa ssb                   >> "$status" 2>&1
    bash run-repeat.sh s.prof.scanedges ssea                 >> "$status" 2>&1
    ;;
  rescan)   # the scan passes again after a scan-profile change (profiler off, then on)
    bash run-repeat.sh s.scan sscan              >> "$status" 2>&1
    bash run-repeat.sh s.scanedges sscanedges    >> "$status" 2>&1
    bash run-repeat.sh s.prof.scan ssa ssb       >> "$status" 2>&1
    bash run-repeat.sh s.prof.scanedges ssea     >> "$status" 2>&1
    ;;
  gate)
    bash run-repeat.sh s.full sfull           >> "$status" 2>&1
    bash run-repeat.sh s sraw                 >> "$status" 2>&1
    bash run-repeat.sh s.scan sscan           >> "$status" 2>&1
    bash run-repeat.sh s.scanedges sscanedges >> "$status" 2>&1
    bash run-repeat.sh s.edges sedges         >> "$status" 2>&1
    ;;
  *)
    bash run-repeat.sh "$stage" "$@" >> "$status" 2>&1
    ;;
esac
echo "DROP2_DONE $(date)" >> "$status"
