#!/usr/bin/env bash
# One-shot wrapper for the output-x64.5 extractor build (binary frames writer),
# launched via a Windows scheduled task: long builds die under the agent Bash
# tool's 600 s timeout. Incremental on the existing essentia-src/build object
# cache (no reconfigure, no clean) like the .4 build was on .3. Logs to
# build-x5.log; the final line is EXIT=<code> of the WAF build.
#   -j6: uncapped -j$(nproc) OOM-kills mingw g++ (~1 GB/process) on a 15 GB WSL VM.
cd "$(dirname "$0")" || exit 1
LOG="$(pwd)/build-x5.log"
echo "START $(date)" > "$LOG"
export PKG_CONFIG_PATH="$(pwd)/3rdparty/lib/pkgconfig"
cd essentia-src || { echo "EXIT=2" >> "$LOG"; exit 2; }
python3 waf -j6 >> "$LOG" 2>&1
rc=$?
echo "WAF_EXIT=$rc" >> "$LOG"
if [ $rc -eq 0 ]; then
  mkdir -p ../output-x64.5
  cp build/src/examples/essentia_streaming_extractor_music.exe ../output-x64.5/ >> "$LOG" 2>&1
  rc=$?
  echo "COPIED to output-x64.5 (cp exit $rc)" >> "$LOG"
  md5sum ../output-x64.5/essentia_streaming_extractor_music.exe >> "$LOG" 2>&1
fi
echo "END $(date)" >> "$LOG"
echo "EXIT=$rc" >> "$LOG"
exit "$rc"   # the caller (schtasks) must see a failed build as failed, not the echo's 0
