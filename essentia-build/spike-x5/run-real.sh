#!/usr/bin/env bash
# Real-audio additive check for one file: runs .4 (2-arg), .5 (2-arg) and
# .5 --frames-bin (3-arg profile + flag) on the same audio, timing each, then
# byte-compares the two .5 main outputs (the frames path must not touch it).
#   run-real.sh <label> <audio file>
# Outputs land in spike-x5/real/<label>.*
cd "$(dirname "$0")" || exit 1
label="$1"; audio="$2"
X4=../output-x64.4/essentia_streaming_extractor_music.exe
X5=../output-x64.5/essentia_streaming_extractor_music.exe
mkdir -p real
run() { # name, exe, args...
  local name="$1"; shift
  local s=${EPOCHREALTIME/./}   # microseconds as an integer (no bc in Git Bash)
  "$@" > "real/$label.$name.log" 2>&1
  local rc=$?
  local e=${EPOCHREALTIME/./}
  local ms=$(( (e - s) / 1000 ))
  printf '%s %s rc=%d %d.%03ds\n' "$label" "$name" "$rc" $((ms / 1000)) $((ms % 1000))
}
run x4   "$X4" "$audio" "real/$label.x4.json"
run x5   "$X5" "$audio" "real/$label.x5.json"
run x5fb "$X5" "$audio" "real/$label.x5fb.json" frames-all.yaml --frames-bin "real/$label.x5fb.bin"
if cmp -s "real/$label.x5.json" "real/$label.x5fb.json"; then
  echo "$label (vi) main JSON byte-identical with and without --frames-bin: YES"
else
  echo "$label (vi) main JSON byte-identical with and without --frames-bin: NO"
fi
ls -l "real/$label.x5fb.bin"
