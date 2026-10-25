#!/usr/bin/env bash
# The production-shaped corpus for the .7 step 0 measurement: what the
# extractor is actually handed when truedat scans these files.
#   make-prod-corpus.sh        (after make-corpus.sh; paths relative to this dir)
#
# truedat converts every source above 44.1 kHz (mono or stereo, ffmpeg present)
# to a 44.1 kHz 32-bit float WAV ONCE and analyses that copy
# (Truedat/Program.cs ShouldPreResample / ResampleTo44k). This applies the same
# rule with the same arguments and production's own ffmpeg
# (dist/truedat/ffmpeg.exe), not the 7.1.1 parity reference:
#   -v error -i <src> -af aresample=resampler=soxr:precision=28 -ar 44100 -c:a pcm_f32le -y <out>.wav
# The rate and channel count come from corpus/probe/<id>.json (ffprobe; truedat
# reads them with TagLib - the same header fields). Rows at or below 44.1 kHz,
# and rows with more than two channels, are passed through unchanged; rows the
# extractor cannot decode (expect = unsupported) stay as they are and fail fast,
# as they do in production before truedat's own transcode retry.
# Output: out/prod/<id>.wav and manifest-prod.tsv (local: it carries the real
# paths), same columns and ids as manifest.tsv, note = "prod: <what was done>".
set -u
cd "$(dirname "$0")" || exit 1
FF=../../dist/truedat/ffmpeg.exe
[ -f manifest.tsv ] || { echo "no manifest.tsv: run make-corpus.sh first"; exit 1; }
mkdir -p out/prod
MAN=manifest-prod.tsv
printf 'id\tkind\tpath\texpect\tnote\n' > "$MAN"
fail=0; converted=0; passed=0
tail -n +2 manifest.tsv | while IFS=$'\t' read -r id kind path expect note; do
  probe="corpus/probe/$id.json"
  rate=$(sed -n 's/.*"sample_rate": "\([0-9]*\)".*/\1/p' "$probe" | head -n 1)
  ch=$(sed -n 's/.*"channels": \([0-9]*\).*/\1/p' "$probe" | head -n 1)
  if [ "$expect" = decode ] && [ -n "$rate" ] && [ "$rate" -gt 44100 ] && [ "${ch:-0}" -ge 1 ] && [ "${ch:-0}" -le 2 ]; then
    out="out/prod/$id.wav"
    if "$FF" -v error -i "$path" -af aresample=resampler=soxr:precision=28 -ar 44100 -c:a pcm_f32le -y "$out"; then
      printf '%s\t%s\t%s\t%s\t%s\n' "$id" "$kind" "$out" "$expect" "prod: converted from $rate Hz ${ch}ch (soxr, f32 WAV)" >> "$MAN"
    else
      echo "CONVERT FAILED: $id"; fail=1
    fi
  else
    printf '%s\t%s\t%s\t%s\t%s\n' "$id" "$kind" "$path" "$expect" "prod: as is (${rate:-?} Hz ${ch:-?}ch)" >> "$MAN"
  fi
done
echo "rows: $(( $(wc -l < "$MAN") - 1 ))  converted: $(grep -c 'prod: converted' "$MAN")  fail=$fail"
exit $fail
