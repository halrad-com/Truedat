#!/usr/bin/env bash
# Builds the x6 B1 decoder-parity corpus and its manifest.
#   make-corpus.sh        (paths are relative to this script's directory)
#
# Synthetic rows: generated with the shipped ffmpeg (dist/truedat/ffmpeg.exe)
# from a deterministic aevalsrc expression (tones + seeded pseudo-noise, plus a
# tone at 0.4 x the rate so hi-res rows carry content above 22 kHz). 3.77 s is
# chosen so no codec's frame size divides it: every row ends on an incomplete
# final frame. 0.05 s rows are shorter than most codecs' priming + one frame.
# *_trunc rows have bytes cut off the end of the file: a genuinely incomplete
# last frame, as a damaged download would have.
#
# Real rows: read in place from a local test bed (never copied, never
# modified; never a library's catalog folder, never an iTunes XML), listed in
# real-files.tsv beside this script - LOCAL ONLY (gitignored), because the
# paths are a machine's own: one row per file, tab-separated,
#   id  absolute path  expect  note
# Without it the corpus is the synthetic rows alone.
#
# Output: corpus/<id>.<ext>, corpus/probe/<id>.json (ffprobe), manifest.tsv
# (local: it carries the real paths) and corpus-md5.txt (tracked: ids + hashes)
#   manifest.tsv columns: id  kind  path  expect  note
#   expect = decode | unsupported (the extractor's FFmpeg has no decoder for it)
set -u
cd "$(dirname "$0")" || exit 1
FF=../../dist/truedat/ffmpeg.exe
FP=../../dist/truedat/ffprobe.exe
mkdir -p corpus/probe
MAN=manifest.tsv
printf 'id\tkind\tpath\texpect\tnote\n' > "$MAN"
fail=0

expr_for() { # rate channels   (TONES=1: the two low tones only)
  local hi="+0.05*sin(2*PI*$(( $1 * 2 / 5 ))*t)"
  local nl="+0.15*(random(0)-0.5)" nr="+0.15*(random(1)-0.5)"
  if [ -n "${TONES:-}" ]; then nl=""; nr=""; hi=""; fi
  local l="0.35*sin(2*PI*997*t)$nl$hi"
  local r="0.35*sin(2*PI*1499*t)$nr$hi"
  if [ "$2" = 1 ]; then echo "$l"; else echo "$l|$r"; fi
}

gen() { # id rate channels duration ext note -- codec args...
  local id="$1" rate="$2" ch="$3" dur="$4" ext="$5" note="$6" expect="$7"; shift 7
  local out="corpus/$id.$ext"
  if "$FF" -hide_banner -loglevel error -y -f lavfi \
       -i "aevalsrc=exprs=$(expr_for "$rate" "$ch"):s=$rate:d=$dur" "$@" "$out"; then
    printf '%s\tsynthetic\t%s\t%s\t%s\n' "$id" "$out" "$expect" "$note" >> "$MAN"
  else
    echo "GEN FAILED: $id ($*)"; fail=1
  fi
}

trunc() { # id source-id ext bytes-to-cut note
  local src="corpus/$2.$3" out="corpus/$1.$3"
  local size; size=$(stat -c %s "$src") || { echo "TRUNC FAILED: $1"; fail=1; return; }
  head -c $(( size - $4 )) "$src" > "$out"
  printf '%s\tsynthetic\t%s\tdecode\t%s\n' "$1" "$out" "$5" >> "$MAN"
}

real() { # id path expect note
  if [ -f "$2" ]; then
    printf '%s\treal\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" >> "$MAN"
  else
    echo "REAL MISSING: $1 $2"; fail=1
  fi
}

D=3.77
# FLAC: every rate, mono/stereo, 16/24-bit, and maximum block size (65535):
# the block size that overflows .5's fixed 384,000-byte conversion buffer.
gen flac16_44k_st   44100 2 $D flac "" decode -c:a flac -sample_fmt s16
gen flac16_44k_mo   44100 1 $D flac "" decode -c:a flac -sample_fmt s16
gen flac16_48k_st   48000 2 $D flac "" decode -c:a flac -sample_fmt s16
gen flac24_96k_st   96000 2 $D flac "" decode -c:a flac -sample_fmt s32
gen flac24_192k_st 192000 2 $D flac "" decode -c:a flac -sample_fmt s32
gen flac16_192k_mo 192000 1 $D flac "" decode -c:a flac -sample_fmt s16
gen flac16_44k_st_bigblock  44100 2 $D flac "block size 65535" decode -c:a flac -sample_fmt s16 -frame_size 65535
# 24-bit big block: two low tones only. With the noise or the 0.4 x rate tone,
# FFmpeg's flac encoder writes a 24-bit 65535-sample stream that no FFmpeg
# (7.1.1 or 8.x) decodes a sample of - an FFmpeg encoder/parser limit, not a
# loader defect, and 0 == 0 would prove nothing.
TONES=1 gen flac24_96k_st_bigblock  96000 2 $D flac "block size 65535, two tones only (see above)" decode -c:a flac -sample_fmt s32 -frame_size 65535
gen flac16_192k_st_bigblock 192000 2 $D flac "block size 65535" decode -c:a flac -sample_fmt s16 -frame_size 65535
# control: 65535 x 1 ch x 4 B = 262,140 B FITS .5's buffer, so any .5 loss on this
# row is the send/receive loop, not the conversion capacity
gen flac16_44k_mo_bigblock  44100 1 $D flac "block size 65535, mono: fits .5 buffer (control)" decode -c:a flac -sample_fmt s16 -frame_size 65535
gen flac16_44k_st_short 44100 2 0.05 flac "0.05 s" decode -c:a flac -sample_fmt s16
# PCM containers (f32 rows take the loader's memcpy path, not swr)
gen wav_s16_44k_st   44100 2 $D wav "" decode -c:a pcm_s16le
gen wav_s24_96k_st   96000 2 $D wav "" decode -c:a pcm_s24le
gen wav_f32_48k_st   48000 2 $D wav "float: memcpy path" decode -c:a pcm_f32le
gen wav_f32_192k_st 192000 2 $D wav "float: memcpy path" decode -c:a pcm_f32le
gen wav_s16_192k_mo 192000 1 $D wav "" decode -c:a pcm_s16le
gen aiff_s16_44k_st  44100 2 $D aiff "" decode -c:a pcm_s16be
# Lossy
gen mp3_44k_st  44100 2 $D mp3 "" decode -c:a libmp3lame -b:a 192k
gen mp3_48k_mo  48000 1 $D mp3 "" decode -c:a libmp3lame -b:a 96k
gen mp3_44k_st_short 44100 2 0.05 mp3 "0.05 s" decode -c:a libmp3lame -b:a 192k
gen aac_44k_st  44100 2 $D m4a "" decode -c:a aac -b:a 192k
gen aac_48k_mo  48000 1 $D m4a "" decode -c:a aac -b:a 96k
gen aac_96k_st  96000 2 $D m4a "" decode -c:a aac -b:a 256k
gen aac_44k_st_short 44100 2 0.05 m4a "0.05 s" decode -c:a aac -b:a 192k
gen aac_44k_st_adts 44100 2 $D aac "raw ADTS, no container length" decode -c:a aac -b:a 192k
gen wma_44k_st  44100 2 $D wma "" decode -c:a wmav2 -b:a 192k
gen wma_48k_st  48000 2 $D wma "" decode -c:a wmav2 -b:a 128k
# Ogg: the muxer draws a random stream serial per run unless bitexact is set,
# so without it these rows would not regenerate byte-identically
gen vorbis_44k_st 44100 2 $D ogg "" decode -c:a libvorbis -q:a 5 -fflags +bitexact
gen vorbis_48k_mo 48000 1 $D ogg "" decode -c:a libvorbis -q:a 3 -fflags +bitexact
gen opus_48k_st 48000 2 $D opus "no opus decoder in the extractor's FFmpeg (plan B4)" unsupported -c:a libopus -b:a 128k -fflags +bitexact
# Lossless in other containers
gen alac16_44k_st   44100 2 $D m4a "" decode -c:a alac -sample_fmt s16p
gen alac24_96k_st   96000 2 $D m4a "" decode -c:a alac -sample_fmt s32p
gen alac24_192k_st 192000 2 $D m4a "" decode -c:a alac -sample_fmt s32p
gen wv_s16_44k_st   44100 2 $D wv "" decode -c:a wavpack -sample_fmt s16p
gen wv_s32_96k_st   96000 2 $D wv "" decode -c:a wavpack -sample_fmt s32p
# Damaged tails
trunc mp3_44k_st_trunc  mp3_44k_st  mp3  700  "last 700 bytes cut"
trunc flac16_44k_st_trunc flac16_44k_st flac 3000 "last 3000 bytes cut"

# real rows from the local list (see header)
if [ -f real-files.tsv ]; then
  while IFS=$'\t' read -r id path expect note; do
    [ -n "$id" ] && real "$id" "$path" "$expect" "$note"
  done < real-files.tsv
else
  echo "no real-files.tsv: synthetic rows only"
fi

# ffprobe every row (codec / container / rate / channels / sample format / duration)
tail -n +2 "$MAN" | while IFS=$'\t' read -r id kind path expect note; do
  "$FP" -v error -select_streams a:0 -show_entries \
    stream=codec_name,sample_rate,channels,sample_fmt,bits_per_raw_sample,duration_ts,time_base:format=format_name,duration \
    -of json "$path" > "corpus/probe/$id.json" || { echo "PROBE FAILED: $id"; }
done

# content pin: id + md5 of every row's file (synthetic rows regenerate
# byte-identically; compare two runs' corpus-md5.txt to prove it)
tail -n +2 "$MAN" | while IFS=$'\t' read -r id kind path expect note; do
  printf '%s\t%s\n' "$id" "$(md5sum < "$path" | cut -c1-32)"
done > corpus-md5.txt

echo "rows: $(( $(wc -l < "$MAN") - 1 ))  fail=$fail"
exit $fail
