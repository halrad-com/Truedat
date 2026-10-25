#!/usr/bin/env bash
# x6 B1 ASan gate. Run INSIDE WSL (wsl.exe bash <this script>):
#   run-asan.sh                     every manifest row  -> pcm/asan/, asan-report.txt
#   run-asan.sh --legacy-capacity   instrument check: the big-block FLAC rows with
#                                   output-x64.5's conversion -> pcm/asan-legacy/,
#                                   asan-legacy-report.txt (MUST report the overflow)
# Builds asan_harness.cpp against the ASan FFmpeg (build-ffmpeg-asan.sh) and the
# TREE's essentia-src/src/essentia/utils/audioloader_decode.h, then decodes under
# AddressSanitizer with LeakSanitizer on. Each row's .info has the parity
# script's format, so `python -I pcm_parity.py asan` compares this run with ffmpeg.
set -u
cd "$(dirname "$0")/.." || exit 1          # spike-x6/
P="$HOME/x6-asan/prefix"
BIN="$HOME/x6-asan/asan_harness"
label=asan; flag=""; report=asan-report.txt
if [ "${1:-}" = "--legacy-capacity" ]; then
  label=asan-legacy; flag=--legacy-capacity; report=asan-legacy-report.txt
fi

libs=$(PKG_CONFIG_PATH="$P/lib/pkgconfig" pkg-config --static --libs libavformat libavcodec libswresample libavutil) \
  || { echo "pkg-config failed"; exit 2; }
# shellcheck disable=SC2086
g++ -std=c++17 -g -O1 -fsanitize=address -fno-omit-frame-pointer \
    -I"$P/include" -I../essentia-src/src/essentia/utils \
    asan/asan_harness.cpp -o "$BIN" $libs || { echo "harness build failed"; exit 2; }

mkdir -p "pcm/$label"
{
  echo "x6 ASan run '$label': $(date)"
  echo "harness: asan/asan_harness.cpp + essentia-src/src/essentia/utils/audioloader_decode.h"
  echo "ffmpeg:  $(head -n 1 asan/build-ffmpeg-asan.log) (--toolchain=gcc-asan --disable-asm)"
  echo "ASAN_OPTIONS=detect_leaks=1"
  echo
} > "$report"

tail -n +2 manifest.tsv | while IFS=$'\t' read -r id kind path expect note; do
  if [ -n "$flag" ]; then case "$id" in *bigblock*) ;; *) continue ;; esac; fi
  case "$path" in /c/*) path="/mnt/c/${path#/c/}" ;; esac
  base="pcm/$label/$id"
  ASAN_OPTIONS=detect_leaks=1 "$BIN" "$path" "$base.f32" $flag > "$base.out" 2> "$base.asan"
  rc=$?
  n=$(grep -c -E 'ERROR: (AddressSanitizer|LeakSanitizer)' "$base.asan")
  { echo "rc=$rc"; cat "$base.out"; grep -v -E '^(==|    #|$)' "$base.asan" | head -n 3; } > "$base.info"
  printf '%-28s rc=%-3d asan_reports=%d  %s\n' "$id" "$rc" "$n" "$(cat "$base.out")" >> "$report"
  if [ "$n" -gt 0 ]; then
    grep -m 1 -E 'ERROR: (AddressSanitizer|LeakSanitizer)' "$base.asan" | sed 's/^/    /' >> "$report"
    grep -m 6 -E '^    #[0-9]' "$base.asan" | sed 's/^/    /' >> "$report"
    grep -m 1 '^SUMMARY:' "$base.asan" | sed 's/^/    /' >> "$report"
  fi
done

rows=$(grep -c -E '^[a-z0-9_]+ +rc=' "$report")
dirty=$(grep -c -E 'asan_reports=[1-9]' "$report")
echo >> "$report"
echo "$rows row(s), $dirty with ASan/LSan reports" >> "$report"
cat "$report"
