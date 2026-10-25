#!/usr/bin/env bash
# Native (Linux, under WSL) FFmpeg built with AddressSanitizer, for the x6 B1
# ASan gate. Same FFmpeg version and the same audio-only configure flags as the
# extractor's MinGW FFmpeg (build_config.sh), plus:
#   --toolchain=gcc-asan  instruments the decoders AND libswresample. The .5
#                         overflow is a write BY swr_convert into the loader's
#                         buffer; ASan cannot see a write made by an
#                         uninstrumented library, so a stock FFmpeg would miss it.
#   --disable-asm         for the same reason: swresample's s16/s32 -> float
#                         conversion has nasm SIMD kernels, which ASan does not
#                         instrument. (Consequence: float decoders may differ in
#                         the last bit from the MinGW build, which keeps its asm;
#                         irrelevant to a memory-safety gate.)
#   --enable-debug        symbolized reports.
# Run inside WSL as a one-shot scheduled task (long):
#   wsl.exe bash <this script>
# Work tree and install prefix: ~/x6-asan (WSL ext4, not /mnt/c). Log beside
# this script; last line EXIT=<rc>. Remove with: rm -rf ~/x6-asan
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
LOG="$HERE/build-ffmpeg-asan.log"
W="$HOME/x6-asan"
PREFIX="$W/prefix"          # set before sourcing: build_config.sh defaults it to the MinGW 3rdparty/
. "$HERE/../../build_config.sh" > /dev/null
echo "START $(date) $FFMPEG_VERSION prefix=$PREFIX" > "$LOG"
mkdir -p "$W" && cd "$W" || { echo "EXIT=2" >> "$LOG"; exit 2; }

if [ ! -f "$FFMPEG_VERSION.tar.gz" ]; then
  curl -sSLO "https://ffmpeg.org/releases/$FFMPEG_VERSION.tar.gz" >> "$LOG" 2>&1 \
    || { echo "download failed"; echo "EXIT=10"; } >> "$LOG"
  [ -f "$FFMPEG_VERSION.tar.gz" ] || exit 10
fi
sha256sum "$FFMPEG_VERSION.tar.gz" >> "$LOG"
rm -rf "$FFMPEG_VERSION" && tar xf "$FFMPEG_VERSION.tar.gz" && cd "$FFMPEG_VERSION" \
  || { echo "EXIT=11" >> "$LOG"; exit 11; }

# shellcheck disable=SC2086  # the flag lists are meant to split
./configure $FFMPEG_AUDIO_FLAGS --prefix="$PREFIX" --enable-static --disable-shared \
    --toolchain=gcc-asan --disable-asm --enable-debug --disable-stripping >> "$LOG" 2>&1
rc=$?
echo "CONFIGURE_EXIT=$rc" >> "$LOG"
if [ $rc -eq 0 ]; then
  make -j16 >> "$LOG" 2>&1 && make install >> "$LOG" 2>&1
  rc=$?
  echo "MAKE_EXIT=$rc" >> "$LOG"
fi
echo "END $(date)" >> "$LOG"
echo "EXIT=$rc" >> "$LOG"
exit $rc
