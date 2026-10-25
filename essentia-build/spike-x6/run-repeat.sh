#!/usr/bin/env bash
# Runs one extractor over every manifest row, once per label, the way truedat
# calls it - the same-binary repeat runs that define the per-field acceptance
# band, the cross-build runs judged against it, and the drop 2 CPU passes.
#   run-repeat.sh <spec> <label> [<label> ...]
# <spec> = <build>[.<mode>]...   (schtasks /tr is capped at 261 characters
# and carries no environment, so everything rides in this one token)
#   build:  x5 = ../output-x64.5   x6 = ../output-x64.6   a = out/build-a
#           p = out/build-p (x6profile build)   s = out/build-s3 (the drop 2 build:
#           cuts + profile-merge fix + one-frame-per-step loader + StereoDemuxer
#           guard; s1 = out/build-s and s2 = out/build-s2 are its intermediates)
#           m1 = out/build-m1 (drop 3 stage 1: peak-working-set in the x6profile
#           header + StereoMuxer guard)   m2 = out/build-m2 (stage 2: no whole-pool
#           copies at the end of MusicExtractor::compute)
#           or a path to an extractor exe
#   modes:  prof        ESSENTIA_PROFILE on: each run appends its per-algorithm
#                       table to out/<label>/profile.tsv (a build without
#                       x6profile ignores the variable)
#           edges       truedat's scan-with-edges call: edges-profile.yaml
#                       --frames-bin out/<label>/<id>.bin
#           scan        3-arg call with scan-profile.yaml
#           scanedges   scan-edges-profile.yaml --frames-bin out/<label>/<id>.bin
#           full        3-arg call with profiles/full.yaml (= the 2-arg call)
#           cut=<name>  3-arg call with profiles/cut-<name>.yaml (one cut alone)
#           prod        run over manifest-prod.tsv (make-prod-corpus.sh: the
#                       hi-res rows converted to 44.1k as truedat does) instead
#                       of manifest.tsv
#   no mode = the plain 2-arg call (input output), the .5-era scan-only shape
#   (truedat calls a scan-profile-capable build with scan/scanedges since 2026-10-25).
# Output: out/<label>/<id>.json (+ .bin, .log) and out/<label>/runs.tsv
# (id, rc, wall ms). Status: out/repeat-<first label>.log, last line REPEAT_DONE.
# Minutes per real track: launch through a one-shot scheduled task.
set -u
cd "$(dirname "$0")" || exit 1
spec="$1"; shift
IFS=. read -r build rest <<< "$spec.x"      # split off the build token
modes=".${rest%.x}."                        # ".prof.edges." for membership tests
case "$build" in
  x5) exe=../output-x64.5/essentia_streaming_extractor_music.exe ;;
  x6) exe=../output-x64.6/essentia_streaming_extractor_music.exe ;;
  a)  exe=out/build-a/essentia_streaming_extractor_music.exe ;;
  p)  exe=out/build-p/essentia_streaming_extractor_music.exe ;;
  s)  exe=out/build-s3/essentia_streaming_extractor_music.exe ;;
  s1) exe=out/build-s/essentia_streaming_extractor_music.exe ;;
  s2) exe=out/build-s2/essentia_streaming_extractor_music.exe ;;
  m1) exe=out/build-m1/essentia_streaming_extractor_music.exe ;;
  m2) exe=out/build-m2/essentia_streaming_extractor_music.exe ;;
  *)  exe="$build" ;;
esac
profile=""; bin=""
case "$modes" in *.edges.*)     profile=edges-profile.yaml; bin=1 ;; esac
case "$modes" in *.scan.*)      profile=scan-profile.yaml ;; esac
case "$modes" in *.scanedges.*) profile=scan-edges-profile.yaml; bin=1 ;; esac
case "$modes" in *.full.*)      profile=profiles/full.yaml ;; esac
case "$modes" in *.cut=*)       cut="${modes#*.cut=}"; profile="profiles/cut-${cut%%.*}.yaml" ;; esac
profiling=""
case "$modes" in *.prof.*) profiling=1 ;; esac
manifest=manifest.tsv
case "$modes" in *.prod.*) manifest=manifest-prod.tsv ;; esac
[ -f "$manifest" ] || { echo "no $manifest"; exit 1; }
[ -z "$profile" ] || [ -f "$profile" ] || { echo "no profile $profile"; exit 1; }

status="out/repeat-$1.log"
mkdir -p out
echo "START $(date) spec=$spec exe=$exe profile=${profile:-(2-arg)} bin=${bin:-0} prof=${profiling:-0} manifest=$manifest labels=$*" > "$status"
md5sum "$exe" >> "$status" 2>&1
for label in "$@"; do
  mkdir -p "out/$label"
  printf 'id\trc\tms\n' > "out/$label/runs.tsv"
  prof=""
  if [ -n "$profiling" ]; then prof="out/$label/profile.tsv"; : > "$prof"; fi
  tail -n +2 "$manifest" | while IFS=$'\t' read -r id kind path expect note; do
    extra=()
    [ -n "$profile" ] && extra=("$profile")
    [ -n "$bin" ] && extra+=(--frames-bin "out/$label/$id.bin")
    s=$(date +%s%N)
    ESSENTIA_PROFILE="$prof" "$exe" "$path" "out/$label/$id.json" "${extra[@]}" > "out/$label/$id.log" 2>&1
    rc=$?
    e=$(date +%s%N)
    printf '%s\t%d\t%d\n' "$id" "$rc" $(( (e - s) / 1000000 )) >> "out/$label/runs.tsv"
  done
  echo "label $label done $(date)" >> "$status"
done
echo "REPEAT_DONE $(date)" >> "$status"
