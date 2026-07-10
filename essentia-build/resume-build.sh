#!/usr/bin/env bash
# Resume the incremental Essentia x64 build with bounded parallelism.
# -j8 caps concurrent mingw g++ jobs: 22 (nproc default) x ~1GB each
# OOM-killed the build on a 15GB WSL VM.
cd "$(dirname "$0")/essentia-src"
export PKG_CONFIG_PATH="$(cd .. && pwd)/3rdparty/lib/pkgconfig"
python3 waf -j8 >> ../build-chordsfix.log 2>&1
rc=$?
echo "WAF EXIT: $rc" >> ../build-chordsfix.log
if [ $rc -eq 0 ]; then
  cp build/src/examples/essentia_streaming_extractor_music.exe ../output-x64/ \
    && echo "EXTRACTOR COPIED to output-x64" >> ../build-chordsfix.log
fi
