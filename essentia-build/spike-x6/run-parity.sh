#!/usr/bin/env bash
# Decodes every manifest row twice - with a reference ffmpeg and with the
# Essentia AudioLoader under test - for pcm_parity.py to compare.
#   run-parity.sh <label> <essentia_standard_audioloader_dump.exe> [same|shipped]
#
# References (see README.md):
#   same    (default, THE GATE) ref/ffmpeg-7.1.1-mingw.exe - built from the
#           extractor's own FFmpeg source, flags and toolchain
#           (ref/build-ffmpeg-ref.sh), so any difference is the loader's
#   shipped (informational) dist/truedat/ffmpeg.exe - FFmpeg 8.x; differs from
#           7.1.1 in float decoders and in AAC end trimming
# Both run  ffmpeg -i <in> -map 0:a:0 -c:a pcm_f32le -f f32le <out>  with NO
# -ar / -ac: the loader converts sample format only (in rate == out rate, in
# layout == out layout), so the reference must too. Encoder delay and end
# padding (AV_PKT_DATA_SKIP_SAMPLES, MOV edit lists) are applied inside
# libavcodec / libavformat on both sides; neither side trims anything itself.
#
# Output: pcm/ref-<ref>/<id>.f32 (+ .log; cached, regenerated when the source is newer),
#         pcm/<label>/<id>.f32 + .info (rc, summary line, stderr; always rerun).
# Files are written to a temp name and renamed, so a killed run never leaves
# a truncated file that looks complete. Safe to rerun.
set -u
cd "$(dirname "$0")" || exit 1
label="$1"; dump="$2"; ref="${3:-same}"
case "$ref" in
  same)    FF=ref/ffmpeg-7.1.1-mingw.exe ;;
  shipped) FF=../../dist/truedat/ffmpeg.exe ;;
  *) echo "reference must be same or shipped"; exit 1 ;;
esac
[ -x "$FF" ] || { echo "no reference ffmpeg at $FF"; exit 1; }
R="pcm/ref-$ref"
mkdir -p "$R" "pcm/$label"
"$FF" -version 2>/dev/null | head -1 > "$R/ffmpeg-version.txt"

tail -n +2 manifest.tsv | while IFS=$'\t' read -r id kind path expect note; do
  rf="$R/$id.f32"
  if [ ! -f "$rf" ] || [ "$path" -nt "$rf" ]; then   # missing, or the source changed since
    if "$FF" -hide_banner -v error -y -i "$path" -map 0:a:0 -c:a pcm_f32le -f f32le "$rf.tmp" \
         > "$R/$id.log" 2>&1; then
      mv "$rf.tmp" "$rf"
    else
      echo "ref rc=$?" >> "$R/$id.log"; rm -f "$rf.tmp"
    fi
  fi

  [ -n "$dump" ] || continue          # reference only (dump given as "")
  out="pcm/$label/$id.f32"
  rm -f "$out"
  summary=$("$dump" "$path" "$out.tmp" 2> "pcm/$label/$id.err")
  rc=$?
  [ -f "$out.tmp" ] && mv "$out.tmp" "$out"
  { echo "rc=$rc"; echo "$summary"; cat "pcm/$label/$id.err"; } > "pcm/$label/$id.info"
  rm -f "pcm/$label/$id.err"
  echo "$label $id rc=$rc $summary"
done
