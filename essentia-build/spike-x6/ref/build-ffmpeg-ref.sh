#!/usr/bin/env bash
# The x6 B1 parity REFERENCE: an ffmpeg.exe built from the same FFmpeg source,
# MinGW cross toolchain and audio-only configure flags as the extractor's own
# libraries (build_config.sh + 3rdparty/build_ffmpeg.sh), so its decoders,
# demuxers and libswresample are the extractor's to the line. Only what the
# CLI needs to write f32le is added - the ffmpeg program, libavfilter with the
# audio plumbing filters, the f32le muxer and pcm_f32le encoder - none of which
# touches decoding.
#
# Why not only the shipped dist/truedat/ffmpeg.exe: it is libavcodec 63
# (FFmpeg 8.x) against the extractor's 61 (7.1.1); its float decoders (aac,
# wmav2) differ by ~1e-7 and its MOV demuxer trims AAC end padding the 7.1.1
# one does not, so a PCM-equality gate against it measures FFmpeg drift, not
# the loader. It stays in the parity tables as an informational column.
#
# Run inside WSL as a one-shot scheduled task:  wsl.exe bash <this script>
# Work tree ~/x6-ref (WSL ext4); output spike-x6/ref/ffmpeg-7.1.1-mingw.exe;
# log beside this script, last line EXIT=<rc>. Remove the work tree with
# rm -rf ~/x6-ref.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
LOG="$HERE/build-ffmpeg-ref.log"
W="$HOME/x6-ref"
PREFIX="$W/prefix"          # set before sourcing: build_config.sh defaults it to the MinGW 3rdparty/
. "$HERE/../../build_config.sh" > /dev/null
echo "START $(date) $FFMPEG_VERSION host=$HOST" > "$LOG"
mkdir -p "$W" && cd "$W" || { echo "EXIT=2" >> "$LOG"; exit 2; }

if [ ! -f "$FFMPEG_VERSION.tar.gz" ]; then
  if [ -f "$HOME/x6-asan/$FFMPEG_VERSION.tar.gz" ]; then
    cp "$HOME/x6-asan/$FFMPEG_VERSION.tar.gz" .
  else
    curl -sSLO "https://ffmpeg.org/releases/$FFMPEG_VERSION.tar.gz" >> "$LOG" 2>&1
  fi
fi
[ -f "$FFMPEG_VERSION.tar.gz" ] || { echo "no tarball"; echo "EXIT=10"; } >> "$LOG"
[ -f "$FFMPEG_VERSION.tar.gz" ] || exit 10
sha256sum "$FFMPEG_VERSION.tar.gz" >> "$LOG"
rm -rf "$FFMPEG_VERSION" && tar xf "$FFMPEG_VERSION.tar.gz" && cd "$FFMPEG_VERSION" \
  || { echo "EXIT=11" >> "$LOG"; exit 11; }

# shellcheck disable=SC2086  # the flag lists are meant to split
./configure \
    $FFMPEG_AUDIO_FLAGS \
    --prefix="$PREFIX" \
    --enable-cross-compile \
    --cross-prefix=$HOST- \
    --arch=x86_64 \
    --target-os=mingw32 \
    $SHARED_OR_STATIC \
    --enable-ffmpeg \
    --enable-avfilter --enable-filter=abuffer,abuffersink,aresample,aformat,anull \
    --enable-muxer=pcm_f32le --enable-encoder=pcm_f32le >> "$LOG" 2>&1
rc=$?
echo "CONFIGURE_EXIT=$rc" >> "$LOG"
if [ $rc -eq 0 ]; then
  make -j16 ffmpeg.exe >> "$LOG" 2>&1 && cp ffmpeg.exe "$HERE/ffmpeg-7.1.1-mingw.exe"
  rc=$?
  echo "MAKE_EXIT=$rc" >> "$LOG"
  md5sum "$HERE/ffmpeg-7.1.1-mingw.exe" >> "$LOG" 2>&1
fi
echo "END $(date)" >> "$LOG"
echo "EXIT=$rc" >> "$LOG"
exit $rc
